using System;
using MinecraftClone3API.Client;
using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    /// <summary>Depth-only framebuffer for the sun shadow pass: a single DepthComponent24 texture, no colour
    /// attachment. Configured as a hardware shadow sampler (CompareRefToTexture + Linear), so every PCF tap
    /// does a free 2x2 bilinear depth comparison. The shadow-resolve pass samples it to produce the sun
    /// shadow factor.</summary>
    public sealed class ShadowFramebuffer : Framebuffer
    {
        public const int ShadowMapSize = 1024;

        private readonly int _depthTexture;

        public ShadowFramebuffer(int size) : base(size, size)
        {
            Bind();

            // Mutable DepthComponent24 storage (GL 4.1). ClampToBorder with a white (1.0) border means samples
            // outside the map read max depth, i.e. never shadowed.
            _depthTexture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _depthTexture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent24, size, size, 0, PixelFormat.DepthComponent, PixelType.UnsignedInt, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToBorder);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToBorder);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBorderColor, new[] {1f, 1f, 1f, 1f});
            // Hardware depth comparison: texture() returns a filtered 0..1 lit factor (2x2 PCF) instead of a
            // raw depth, so each Poisson tap is already bilinear-filtered.
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)All.Lequal);

            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, _depthTexture, 0);
            GL.DrawBuffer(DrawBufferMode.None);
            GL.ReadBuffer(ReadBufferMode.None);
            CheckFramebufferStatus();

            GraphicsDebug.Label(ObjectLabelIdentifier.Framebuffer, _id, "ShadowMap.FBO");
            GraphicsDebug.Label(ObjectLabelIdentifier.Texture, _depthTexture, "ShadowMap.Depth");

            Unbind(ClientResources.Window.FramebufferSize.X, ClientResources.Window.FramebufferSize.Y);
        }

        public void BindDepthTexture(TextureUnit unit)
        {
            GL.ActiveTexture(unit);
            GL.BindTexture(TextureTarget.Texture2D, _depthTexture);
            // Clear any sampler object bound to this unit (units 0-3 use Samplers) so the texture's own
            // filtering/border/compare params apply.
            GL.BindSampler(unit - TextureUnit.Texture0, 0);
        }

        public override void Dispose()
        {
            base.Dispose();
            GL.DeleteTexture(_depthTexture);
        }
    }
}
