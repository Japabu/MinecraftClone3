using System;
using System.Collections.Generic;
using System.IO;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    public class Shader
    {
        public static Dictionary<string, ShaderType> ShaderTypes = new Dictionary<string, ShaderType>()
        {
            {".fs", ShaderType.FragmentShader},
            {".vs", ShaderType.VertexShader},
            {".gs", ShaderType.GeometryShader}
        };


        private readonly int _programId;
        
        public Shader(string resourcePath)
        {
            _programId = GL.CreateProgram();

            var exists = false;
            foreach (var entry in ShaderTypes)
            {
                var path = resourcePath + entry.Key;
                if (ResourceReader.Exists(path))
                {
                    AttachShader(entry.Value, ResourceReader.ReadString(path));
                    exists = true;
                }
            }

            if (!exists)
            {
                Logger.Error($"Shader \"{resourcePath}\" was not found!");
                return;
            }

            GL.LinkProgram(_programId);
            var infoLog = GL.GetProgramInfoLog(_programId);
            if (!string.IsNullOrEmpty(infoLog))
                Logger.Error($"There was an error linking shader \"{resourcePath}\": {infoLog}");
        }

        public void Bind() => GL.UseProgram(_programId);

        public int GetUniformLocation(string name) => GL.GetUniformLocation(_programId, name);


        private void AttachShader(ShaderType type, string source)
        {
            var id = GL.CreateShader(type);
            GL.ShaderSource(id, source);
            GL.CompileShader(id);
            GL.AttachShader(_programId, id);
        }
    }
}
