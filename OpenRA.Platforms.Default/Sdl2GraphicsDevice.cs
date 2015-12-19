#region Copyright & License Information
/*
 * Copyright 2007-2015 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation. For more information,
 * see COPYING.
 */
#endregion

using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using OpenRA;
using OpenRA.Graphics;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL;
using SDL2;

namespace OpenRA.Platforms.Default
{
	sealed class Sdl2GraphicsDevice : ThreadAffine, IGraphicsDevice
	{
		readonly Sdl2Input input;
		IntPtr context, window;
		bool disposed;

		public Size WindowSize { get; private set; }

		public Sdl2GraphicsDevice(Size windowSize, WindowMode windowMode)
		{
			Console.WriteLine("Using SDL 2 with OpenGL renderer");
			WindowSize = windowSize;

			SDL.SDL_Init(SDL.SDL_INIT_NOPARACHUTE | SDL.SDL_INIT_VIDEO);
			SDL.SDL_GL_SetAttribute(SDL.SDL_GLattr.SDL_GL_DOUBLEBUFFER, 1);
			SDL.SDL_GL_SetAttribute(SDL.SDL_GLattr.SDL_GL_RED_SIZE, 8);
			SDL.SDL_GL_SetAttribute(SDL.SDL_GLattr.SDL_GL_GREEN_SIZE, 8);
			SDL.SDL_GL_SetAttribute(SDL.SDL_GLattr.SDL_GL_BLUE_SIZE, 8);
			SDL.SDL_GL_SetAttribute(SDL.SDL_GLattr.SDL_GL_ALPHA_SIZE, 0);

			SDL.SDL_DisplayMode display;
			SDL.SDL_GetCurrentDisplayMode(0, out display);

			Console.WriteLine("Desktop resolution: {0}x{1}", display.w, display.h);
			if (WindowSize.Width == 0 && WindowSize.Height == 0)
			{
				Console.WriteLine("No custom resolution provided, using desktop resolution");
				WindowSize = new Size(display.w, display.h);
			}

			Console.WriteLine("Using resolution: {0}x{1}", WindowSize.Width, WindowSize.Height);

			window = SDL.SDL_CreateWindow("OpenRA", SDL.SDL_WINDOWPOS_CENTERED, SDL.SDL_WINDOWPOS_CENTERED,
				WindowSize.Width, WindowSize.Height, SDL.SDL_WindowFlags.SDL_WINDOW_OPENGL);

			if (Game.Settings.Game.LockMouseWindow)
				GrabWindowMouseFocus();
			else
				ReleaseWindowMouseFocus();

			if (windowMode == WindowMode.Fullscreen)
				SDL.SDL_SetWindowFullscreen(window, (uint)SDL.SDL_WindowFlags.SDL_WINDOW_FULLSCREEN);
			else if (windowMode == WindowMode.PseudoFullscreen)
			{
				// Work around a visual glitch in OSX: the window is offset
				// partially offscreen if the dock is at the left of the screen
				if (Platform.CurrentPlatform == PlatformType.OSX)
					SDL.SDL_SetWindowPosition(window, 0, 0);

				SDL.SDL_SetWindowFullscreen(window, (uint)SDL.SDL_WindowFlags.SDL_WINDOW_FULLSCREEN_DESKTOP);
				SDL.SDL_SetHint(SDL.SDL_HINT_VIDEO_MINIMIZE_ON_FOCUS_LOSS, "0");
			}

			context = SDL.SDL_GL_CreateContext(window);
			if (context == IntPtr.Zero || SDL.SDL_GL_MakeCurrent(window, context) < 0)
				throw new InvalidOperationException("Can not create OpenGL context. (Error: {0})".F(SDL.SDL_GetError()));

			GraphicsContext.CurrentContext = context;

			GL.LoadAll();
			ErrorHandler.CheckGlVersion();
			ErrorHandler.CheckGlError();

			if (SDL.SDL_GL_ExtensionSupported("GL_EXT_framebuffer_object") == SDL.SDL_bool.SDL_FALSE)
			{
				ErrorHandler.WriteGraphicsLog("OpenRA requires the OpenGL extension GL_EXT_framebuffer_object.\n"
					+ "Please try updating your GPU driver to the latest version provided by the manufacturer.");
				throw new InvalidProgramException("Missing OpenGL extension GL_EXT_framebuffer_object. See graphics.log for details.");
			}

			GL.EnableClientState(ArrayCap.VertexArray);
			ErrorHandler.CheckGlError();
			GL.EnableClientState(ArrayCap.TextureCoordArray);
			ErrorHandler.CheckGlError();

			SDL.SDL_SetModState(SDL.SDL_Keymod.KMOD_NONE);
			input = new Sdl2Input();
		}

		public IHardwareCursor CreateHardwareCursor(string name, Size size, byte[] data, int2 hotspot)
		{
			VerifyThreadAffinity();
			try
			{
				return new SDL2HardwareCursor(size, data, hotspot);
			}
			catch (Exception ex)
			{
				throw new InvalidDataException("Failed to create hardware cursor `{0}` - {1}".F(name, ex.Message), ex);
			}
		}

		public void SetHardwareCursor(IHardwareCursor cursor)
		{
			VerifyThreadAffinity();
			var c = cursor as SDL2HardwareCursor;
			if (c == null)
				SDL.SDL_ShowCursor((int)SDL.SDL_bool.SDL_FALSE);
			else
			{
				SDL.SDL_ShowCursor((int)SDL.SDL_bool.SDL_TRUE);
				SDL.SDL_SetCursor(c.Cursor);
			}
		}

		sealed class SDL2HardwareCursor : IHardwareCursor
		{
			public IntPtr Cursor { get; private set; }
			IntPtr surface;

			public SDL2HardwareCursor(Size size, byte[] data, int2 hotspot)
			{
				try
				{
					surface = SDL.SDL_CreateRGBSurface(0, size.Width, size.Height, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000);
					if (surface == IntPtr.Zero)
						throw new InvalidDataException("Failed to create surface: {0}".F(SDL.SDL_GetError()));

					var sur = (SDL.SDL_Surface)Marshal.PtrToStructure(surface, typeof(SDL.SDL_Surface));
					Marshal.Copy(data, 0, sur.pixels, data.Length);

					// This call very occasionally fails on Windows, but often works when retried.
					for (var retries = 0; retries < 3 && Cursor == IntPtr.Zero; retries++)
						Cursor = SDL.SDL_CreateColorCursor(surface, hotspot.X, hotspot.Y);
					if (Cursor == IntPtr.Zero)
						throw new InvalidDataException("Failed to create cursor: {0}".F(SDL.SDL_GetError()));
				}
				catch
				{
					Dispose();
					throw;
				}
			}

			~SDL2HardwareCursor()
			{
				Game.RunAfterTick(() => Dispose(false));
			}

			public void Dispose()
			{
				Game.RunAfterTick(() => Dispose(true));
				GC.SuppressFinalize(this);
			}

			void Dispose(bool disposing)
			{
				if (Cursor != IntPtr.Zero)
				{
					SDL.SDL_FreeCursor(Cursor);
					Cursor = IntPtr.Zero;
				}

				if (surface != IntPtr.Zero)
				{
					SDL.SDL_FreeSurface(surface);
					surface = IntPtr.Zero;
				}
			}
		}

		public void Dispose()
		{
			if (disposed)
				return;

			disposed = true;
			if (context != IntPtr.Zero)
			{
				SDL.SDL_GL_DeleteContext(context);
				context = IntPtr.Zero;
			}

			if (window != IntPtr.Zero)
			{
				SDL.SDL_DestroyWindow(window);
				window = IntPtr.Zero;
			}

			SDL.SDL_Quit();
		}

		static BeginMode ModeFromPrimitiveType(PrimitiveType pt)
		{
			switch (pt)
			{
				case PrimitiveType.PointList: return BeginMode.Points;
				case PrimitiveType.LineList: return BeginMode.Lines;
				case PrimitiveType.TriangleList: return BeginMode.Triangles;
			}

			throw new NotImplementedException();
		}

		public void DrawPrimitives(PrimitiveType pt, int firstVertex, int numVertices)
		{
			VerifyThreadAffinity();
			GL.DrawArrays(ModeFromPrimitiveType(pt), firstVertex, numVertices);
			ErrorHandler.CheckGlError();
		}

		public void Clear()
		{
			VerifyThreadAffinity();
			GL.ClearColor(0, 0, 0, 1);
			ErrorHandler.CheckGlError();
			GL.Clear(ClearBufferMask.ColorBufferBit);
			ErrorHandler.CheckGlError();
		}

		public void EnableDepthBuffer()
		{
			VerifyThreadAffinity();
			GL.Clear(ClearBufferMask.DepthBufferBit);
			ErrorHandler.CheckGlError();
			GL.Enable(EnableCap.DepthTest);
			ErrorHandler.CheckGlError();
		}

		public void DisableDepthBuffer()
		{
			VerifyThreadAffinity();
			GL.Disable(EnableCap.DepthTest);
			ErrorHandler.CheckGlError();
		}

		public void SetBlendMode(BlendMode mode)
		{
			VerifyThreadAffinity();
			GL.BlendEquation(BlendEquationMode.FuncAdd);
			ErrorHandler.CheckGlError();

			switch (mode)
			{
				case BlendMode.None:
					GL.Disable(EnableCap.Blend);
					break;
				case BlendMode.Alpha:
					GL.Enable(EnableCap.Blend);
					ErrorHandler.CheckGlError();
					GL.BlendFunc(BlendingFactorSrc.One, BlendingFactorDest.OneMinusSrcAlpha);
					break;
				case BlendMode.Additive:
				case BlendMode.Subtractive:
					GL.Enable(EnableCap.Blend);
					ErrorHandler.CheckGlError();
					GL.BlendFunc(BlendingFactorSrc.One, BlendingFactorDest.One);
					if (mode == BlendMode.Subtractive)
					{
						ErrorHandler.CheckGlError();
						GL.BlendEquation(BlendEquationMode.FuncReverseSubtract);
					}

					break;
				case BlendMode.Multiply:
					GL.Enable(EnableCap.Blend);
					ErrorHandler.CheckGlError();
					GL.BlendFunc(BlendingFactorSrc.DstColor, BlendingFactorDest.OneMinusSrcAlpha);
					ErrorHandler.CheckGlError();
					break;
				case BlendMode.Multiplicative:
					GL.Enable(EnableCap.Blend);
					ErrorHandler.CheckGlError();
					GL.BlendFunc(BlendingFactorSrc.Zero, BlendingFactorDest.SrcColor);
					break;
				case BlendMode.DoubleMultiplicative:
					GL.Enable(EnableCap.Blend);
					ErrorHandler.CheckGlError();
					GL.BlendFunc(BlendingFactorSrc.DstColor, BlendingFactorDest.SrcColor);
					break;
			}

			ErrorHandler.CheckGlError();
		}

		public void GrabWindowMouseFocus()
		{
			VerifyThreadAffinity();
			SDL.SDL_SetWindowGrab(window, SDL.SDL_bool.SDL_TRUE);
		}

		public void ReleaseWindowMouseFocus()
		{
			VerifyThreadAffinity();
			SDL.SDL_SetWindowGrab(window, SDL.SDL_bool.SDL_FALSE);
		}

		public void EnableScissor(int left, int top, int width, int height)
		{
			VerifyThreadAffinity();

			if (width < 0)
				width = 0;

			if (height < 0)
				height = 0;

			GL.Scissor(left, WindowSize.Height - (top + height), width, height);
			ErrorHandler.CheckGlError();
			GL.Enable(EnableCap.ScissorTest);
			ErrorHandler.CheckGlError();
		}

		public void DisableScissor()
		{
			VerifyThreadAffinity();
			GL.Disable(EnableCap.ScissorTest);
			ErrorHandler.CheckGlError();
		}

		public void SetLineWidth(float width)
		{
			VerifyThreadAffinity();
			GL.LineWidth(width);
			ErrorHandler.CheckGlError();
		}

		public Bitmap TakeScreenshot()
		{
			var rect = new Rectangle(Point.Empty, WindowSize);
			var bitmap = new Bitmap(rect.Width, rect.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
			var data = bitmap.LockBits(rect,
				System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

			GL.PushClientAttrib(ClientAttribMask.ClientPixelStoreBit);

			GL.PixelStore(PixelStoreParameter.PackRowLength, data.Stride / 4f);
			GL.PixelStore(PixelStoreParameter.PackAlignment, 1);

			GL.ReadPixels(rect.X, rect.Y, rect.Width, rect.Height, PixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);
			GL.Finish();

			GL.PopClientAttrib();

			bitmap.UnlockBits(data);

			// OpenGL standard defines the origin in the bottom left corner which is why this is upside-down by default.
			bitmap.RotateFlip(RotateFlipType.RotateNoneFlipY);

			return bitmap;
		}

		public void Present()
		{
			VerifyThreadAffinity();
			SDL.SDL_GL_SwapWindow(window);
		}

		public void PumpInput(IInputHandler inputHandler)
		{
			VerifyThreadAffinity();
			input.PumpInput(inputHandler);
		}

		public string GetClipboardText()
		{
			VerifyThreadAffinity();
			return input.GetClipboardText();
		}

		public bool SetClipboardText(string text)
		{
			VerifyThreadAffinity();
			return input.SetClipboardText(text);
		}

		public IVertexBuffer<Vertex> CreateVertexBuffer(int size)
		{
			VerifyThreadAffinity();
			return new VertexBuffer<Vertex>(size);
		}

		public ITexture CreateTexture()
		{
			VerifyThreadAffinity();
			return new Texture();
		}

		public ITexture CreateTexture(Bitmap bitmap)
		{
			VerifyThreadAffinity();
			return new Texture(bitmap);
		}

		public IFrameBuffer CreateFrameBuffer(Size s)
		{
			VerifyThreadAffinity();
			return new FrameBuffer(s);
		}

		public IShader CreateShader(string name)
		{
			VerifyThreadAffinity();
			return new Shader(name);
		}
	}
}
