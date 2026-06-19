using System;
using MinecraftClone3API.Client;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    public sealed class GeometryFramebuffer : Framebuffer
    {
        private readonly int _diffuse;
        private readonly int _normal;
        private readonly int _light;
        private readonly int _depth;

        public GeometryFramebuffer(int width, int height) : base(width, height)
        {
            Bind();

            // Mutable TexImage2D storage (GL 4.1) is used instead of TexStorage2D (GL 4.2),
            // since macOS caps OpenGL at 4.1. Nearest filtering keeps the attachments
            // texture-complete when sampled in the composition pass.
            _diffuse = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _diffuse);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            SetNearest();
            GL.FramebufferTexture(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, _diffuse, 0);

            _normal = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _normal);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            SetNearest();
            GL.FramebufferTexture(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1, _normal, 0);

            // Rgba8: rgb = baked block light, a = baked sky-light factor (composition multiplies a by the
            // sun colour for the dynamic day/night cycle).
            _light = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _light);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            SetNearest();
            GL.FramebufferTexture(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment2, _light, 0);

            _depth = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _depth);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent24, width, height, 0, PixelFormat.DepthComponent, PixelType.UnsignedInt, IntPtr.Zero);
            SetNearest();
            GL.FramebufferTexture(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, _depth, 0);

            GL.DrawBuffers(3, new []{DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2});
            CheckFramebufferStatus();

            Unbind(ClientResources.Window.FramebufferSize.X, ClientResources.Window.FramebufferSize.Y);
        }

        private static void SetNearest()
        {
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        }

        public void BindTexturesAndSamplers()
        {
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _diffuse);
            Samplers.BindFramebufferTextureSampler(0);

            GL.ActiveTexture(TextureUnit.Texture1);
            GL.BindTexture(TextureTarget.Texture2D, _normal);
            Samplers.BindFramebufferTextureSampler(1);

            GL.ActiveTexture(TextureUnit.Texture2);
            GL.BindTexture(TextureTarget.Texture2D, _depth);
            Samplers.BindFramebufferTextureSampler(2);

            GL.ActiveTexture(TextureUnit.Texture3);
            GL.BindTexture(TextureTarget.Texture2D, _light);
            Samplers.BindFramebufferTextureSampler(3);
        }

        public override void Dispose()
        {
            base.Dispose();
            GL.DeleteTexture(_diffuse);
            GL.DeleteTexture(_normal);
            GL.DeleteTexture(_light);
            GL.DeleteTexture(_depth);
        }
    }
}
