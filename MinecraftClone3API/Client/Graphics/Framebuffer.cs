using MinecraftClone3API.Util;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    public abstract class Framebuffer
    {
        protected readonly int _id;

        public readonly int Width;
        public readonly int Height;

        protected Framebuffer(int width, int height)
        {
            _id = GL.GenFramebuffer();

            Width = width;
            Height = height;
        }

        public virtual void Bind()
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _id);
            GL.Viewport(0, 0, Width, Height);
        }

        public virtual void Dispose()
        {
            GL.DeleteFramebuffer(_id);
        }

        public void Unbind(int viewportWidth, int viewportHeight)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.Viewport(0, 0, viewportWidth, viewportHeight);
        }

        public void Clear(Color4 color)
        {
            GL.ClearColor(color);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
        }

        protected void CheckFramebufferStatus()
        {
            var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != FramebufferErrorCode.FramebufferComplete)
                Logger.Error("Error creating geometry framebuffer!");
        }
    }
}
