using System;
using MinecraftClone3API.Client;
using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    /// <summary>Depth-only framebuffer for the cascaded sun shadow pass: a Texture2DArray of
    /// <see cref="MaxCascadeCount"/> depth layers (one per cascade), no colour attachment. Configured as a
    /// hardware shadow sampler (CompareRefToTexture + Linear), so every composition tap does a free 2x2
    /// bilinear depth comparison; a 3x3 tap grid then yields a 6x6-effective soft PCF kernel. The
    /// composition shader samples it to attenuate the sky-light/sun term.</summary>
    public sealed class ShadowFramebuffer : Framebuffer
    {
        public const int ShadowMapSize = 1024;

        // Upper bound on cascades — the depth array always allocates this many layers, so the runtime cascade
        // count (WorldRenderer.CascadeCount, from GraphicsSettings) can change without reallocating. Must match
        // MAX_CASCADES in Composition.fs and bound WorldRenderer.MaxCascadeCount.
        public const int MaxCascadeCount = 4;

        private readonly int _depthArray;

        public ShadowFramebuffer(int size) : base(size, size)
        {
            Bind();

            // Mutable TexImage3D storage (GL 4.1), one DepthComponent24 layer per cascade. ClampToBorder
            // with a white (1.0) border means samples outside a cascade read max depth, i.e. never shadowed.
            _depthArray = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2DArray, _depthArray);
            GL.TexImage3D(TextureTarget.Texture2DArray, 0, PixelInternalFormat.DepthComponent24, size, size, MaxCascadeCount, 0, PixelFormat.DepthComponent, PixelType.UnsignedInt, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToBorder);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToBorder);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureBorderColor, new[] {1f, 1f, 1f, 1f});
            // Hardware depth comparison: texture() returns a filtered 0..1 lit factor (2x2 PCF) instead of
            // a raw depth, so the composition's 3x3 tap grid becomes a 6x6-effective soft kernel.
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureCompareFunc, (int)All.Lequal);

            // Attach layer 0 only to validate completeness; DrawShadowMap re-attaches each layer per frame.
            GL.FramebufferTextureLayer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, _depthArray, 0, 0);
            GL.DrawBuffer(DrawBufferMode.None);
            GL.ReadBuffer(ReadBufferMode.None);
            CheckFramebufferStatus();

            GraphicsDebug.Label(ObjectLabelIdentifier.Framebuffer, _id, "ShadowMap.FBO");
            GraphicsDebug.Label(ObjectLabelIdentifier.Texture, _depthArray, "ShadowMap.DepthArray");

            Unbind(ClientResources.Window.FramebufferSize.X, ClientResources.Window.FramebufferSize.Y);
        }

        /// <summary>Binds this FBO and points its depth attachment at one cascade layer, ready to be
        /// cleared and rendered into.</summary>
        public void BindLayer(int cascade)
        {
            Bind();
            GL.FramebufferTextureLayer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, _depthArray, 0, cascade);
        }

        public void BindDepthTexture(TextureUnit unit)
        {
            GL.ActiveTexture(unit);
            GL.BindTexture(TextureTarget.Texture2DArray, _depthArray);
            // Clear any sampler object bound to this unit (units 0-3 use Samplers) so the texture's own
            // filtering/border/compare params apply.
            GL.BindSampler(unit - TextureUnit.Texture0, 0);
        }

        public override void Dispose()
        {
            base.Dispose();
            GL.DeleteTexture(_depthArray);
        }
    }
}
