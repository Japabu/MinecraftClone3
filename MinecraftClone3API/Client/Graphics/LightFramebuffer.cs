using System;
using MinecraftClone3API.Client;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    public class TextureFramebuffer : Framebuffer
    {
        public readonly Texture Texture;

        private readonly int _depthId = -1;

        public TextureFramebuffer(int width, int height, bool depthBuffer) : base(width, height)
        {
            Bind();

            Texture = Texture.FromId(GL.GenTexture(), width, height);
            Texture.Bind(TextureUnit.Texture0);
            // TexImage2D (GL 4.1) instead of TexStorage2D (GL 4.2) — macOS caps at 4.1.
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.FramebufferTexture(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, Texture.Id, 0);

            if (depthBuffer)
            {
                _depthId = GL.GenRenderbuffer();
                GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthId);
                GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Depth24Stencil8, width, height);
                GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment,
                    RenderbufferTarget.Renderbuffer, _depthId);
            }

            GL.DrawBuffers(1, new[] { DrawBuffersEnum.ColorAttachment0 });
            CheckFramebufferStatus();

            Unbind(ClientResources.Window.FramebufferSize.X, ClientResources.Window.FramebufferSize.Y);
        }
    }
}
