using System.Collections.Generic;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    public static class WorldRenderer
    {
        //Changable in settings
        public const float RenderDistance = 256;
        public const float RenderDistanceSq = RenderDistance * RenderDistance;

        //Changeable in settings
        public const float SortDistance = 128;
        public const float SortDistanceSq = SortDistance * SortDistance;

        public static void RenderWorld(WorldClient world, Matrix4 projection)
        {
            var viewProjection = PlayerController.Camera.View * projection;
            var viewFrustum = Frustum.FromViewProjection(viewProjection);

            var wireframe = false;
            if (wireframe) GL.PolygonMode(TriangleFace.Front, PolygonMode.Line);

            DrawGeometryFramebuffer(world, PlayerController.Camera, projection, viewFrustum);

            if (wireframe) GL.PolygonMode(TriangleFace.Front, PolygonMode.Fill);

            DrawComposition();
        }

        private static void DrawGeometryFramebuffer(WorldClient world, Camera camera, Matrix4 projection, Frustum viewFrustum)
        {
            var chunksToDraw = new List<ChunkRenderData>(1024);
            var transparentSortedChunks = new List<ChunkRenderData>(1024);
            var transparentChunks = new List<ChunkRenderData>(1024);

            foreach (var entry in world.RenderData)
            {
                //Check if chunk is in player view frustum
                var chunkMiddle = (entry.Key * Chunk.Size + new Vector3i(Chunk.Size / 2)).ToVector3();

                if (!viewFrustum.SpehereIntersection(chunkMiddle, Chunk.Radius))
                    continue;

                var lengthSq = (camera.Position - chunkMiddle).LengthSquared;
                if (lengthSq > RenderDistanceSq) continue;

                if (entry.Value.HasTransparency)
                {
                    if (lengthSq < SortDistanceSq)
                    {
                        entry.Value.SortTransparentFaces();
                        transparentSortedChunks.Add(entry.Value);
                    }
                    else
                    {
                        transparentChunks.Add(entry.Value);
                    }
                }
                else chunksToDraw.Add(entry.Value);
            }

            //Sort transparent chunks and append to draw list
            var cameraPos = camera.Position;
            transparentSortedChunks.Sort((chunk1, chunk2)
                => (int)((cameraPos - chunk2.Middle).LengthSquared * 1000 -
                          (cameraPos - chunk1.Middle).LengthSquared * 1000));

            transparentChunks.AddRange(transparentSortedChunks);
            chunksToDraw.AddRange(transparentChunks);

            RenderState.Set(new GlState {CullFace = true, DepthTest = true, DepthFunc = DepthFunction.Lequal});

            ClientResources.GeometryFramebuffer.Bind();
            ClientResources.GeometryFramebuffer.Clear(Color4.DarkBlue);
            //ClientResources.GeometryFramebuffer.Clear(Color4.Transparent);    Breaks transparency

            var shader = ClientResources.WorldGeometryShader;
            shader.Bind();
            // Uniform/sampler locations are queried by name (GLSL 4.10 has no explicit
            // layout(location=)/layout(binding=) for uniforms; macOS caps OpenGL at 4.1).
            var uWorld = shader.GetUniformLocation("uWorld");
            var uCutoff = shader.GetUniformLocation("uCutoff");
            GL.UniformMatrix4(shader.GetUniformLocation("uView"), false, ref camera.View);
            GL.UniformMatrix4(shader.GetUniformLocation("uProjection"), false, ref projection);
            GL.Uniform1(uCutoff, 1);
            GL.Uniform1(shader.GetUniformLocation("uTextures16"), 0);
            GL.Uniform1(shader.GetUniformLocation("uTextures64"), 1);
            GL.Uniform1(shader.GetUniformLocation("uTextures256"), 2);
            GL.Uniform1(shader.GetUniformLocation("uTextures1024"), 3);

            BlockTextureManager.Bind();
            Samplers.BindBlockTextureSampler();

            //Draw opaque blocks front to back
            foreach (var chunk in chunksToDraw)
            {
                var worldMat = Matrix4.CreateTranslation(chunk.Chunk.Position.X * Chunk.Size, chunk.Chunk.Position.Y * Chunk.Size,
                    chunk.Chunk.Position.Z * Chunk.Size);
                GL.UniformMatrix4(uWorld, false, ref worldMat);
                chunk.Draw();
            }

            RenderState.Set(new GlState
            {
                CullFace = true, DepthTest = true, DepthFunc = DepthFunction.Lequal,
                Blend = true, BlendFunc = (BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha)
            });
            GL.Uniform1(uCutoff, 0);

            //Draw transparent blocks back to front
            foreach (var chunk in transparentChunks)
            {
                var worldMat = Matrix4.CreateTranslation(chunk.Chunk.Position.X * Chunk.Size, chunk.Chunk.Position.Y * Chunk.Size,
                    chunk.Chunk.Position.Z * Chunk.Size);
                GL.UniformMatrix4(uWorld, false, ref worldMat);
                chunk.DrawTransparent();
            }

            RenderState.Set(new GlState {CullFace = true, DepthTest = true, DepthFunc = DepthFunction.Lequal});

            EntityRenderer.Render(world, camera, projection);
            PlayerController.Render(camera, projection);
            ChunkBorderRenderer.Render(camera, projection);

            ClientResources.GeometryFramebuffer.Unbind(ClientResources.Window.FramebufferSize.X, ClientResources.Window.FramebufferSize.Y);
        }

        private static void DrawLightFramebuffer(WorldServer world, Matrix4 viewProjectionInv, Frustum viewFrustum)
        {
            //TODO: Lighting?

            /*
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactorSrc.One, BlendingFactorDest.One);

            ClientResources.LightFramebuffer.Bind();
            ClientResources.LightFramebuffer.Clear(Color4.Black);

            ClientResources.PointLightShader.Bind();
            GL.UniformMatrix4(0, false, ref viewProjectionInv);

            
            foreach (var light in world.Lights.Select(light => light as PointLight))
            {
                if (light == null || !viewFrustum.SpehereIntersection(light.Position, light.Range)) continue;
                GL.Uniform3(4, light.Position);
                GL.Uniform3(5, light.Color);
                GL.Uniform1(6, light.Range);
                ClientResources.ScreenRectVao.Draw();
            }
            
            ClientResources.LightFramebuffer.Unbind(Program.Window.FramebufferSize.X, Program.Window.FramebufferSize.Y);

            GL.Disable(EnableCap.Blend);
            */
        }

        private static void DrawComposition()
        {
            GL.ClearColor(Color4.DarkBlue);
            GL.Clear(ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit | ClearBufferMask.ColorBufferBit);

            //GL.Enable(EnableCap.Blend);

            // BindTexturesAndSamplers binds the geometry light buffer (baked block lighting) to
            // unit 3, so the separate deferred LightFramebuffer is no longer bound here.
            ClientResources.GeometryFramebuffer.BindTexturesAndSamplers();
            var comp = ClientResources.CompositionShader;
            comp.Bind();
            GL.Uniform1(comp.GetUniformLocation("uDiffuse"), 0);
            GL.Uniform1(comp.GetUniformLocation("uNormal"), 1);
            GL.Uniform1(comp.GetUniformLocation("uDepth"), 2);
            GL.Uniform1(comp.GetUniformLocation("uLight"), 3);
            ClientResources.ScreenRectVao.Draw();

            //GL.Disable(EnableCap.Blend);
        }
    }
}