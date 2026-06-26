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

        // Uniform locations are stable for the life of a linked program. GetUniformLocation otherwise marshals
        // the managed name to native UTF-8 and does a driver name lookup on EVERY call — and the renderer looks
        // up ~50 uniforms by name every frame. Memoize so it costs one lookup + dictionary hit thereafter (off
        // the render-thread critical path + kills the per-frame string-marshal Gen0 allocations).
        private readonly Dictionary<string, int> _uniformLocations = new Dictionary<string, int>();

        public Shader(string resourcePath)
        {
            _programId = GL.CreateProgram();

            var exists = false;
            foreach (var entry in ShaderTypes)
            {
                var path = resourcePath + entry.Key;
                if (ResourceReader.Exists(path))
                {
                    AttachShader(path, entry.Value, ResourceReader.ReadString(path));
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

            GraphicsDebug.Label(ObjectLabelIdentifier.Program, _programId, Path.GetFileName(resourcePath));
        }

        public void Bind() => GL.UseProgram(_programId);

        public int GetUniformLocation(string name)
        {
            if (_uniformLocations.TryGetValue(name, out var loc)) return loc;
            loc = GL.GetUniformLocation(_programId, name);
            _uniformLocations[name] = loc;
            return loc;
        }


        private void AttachShader(string path, ShaderType type, string source)
        {
            // GLSL is ASCII, but a non-ASCII char (e.g. an em-dash in a comment) makes the string's UTF-8
            // byte length exceed its char length; OpenTK's GL.ShaderSource passes the char length, so GL
            // reads a truncated source and silently drops the tail (often the closing brace), failing as a
            // cryptic "unexpected end of file". Forcing ASCII keeps byte and char lengths in step.
            source = ToAscii(source, path);

            var id = GL.CreateShader(type);
            GL.ShaderSource(id, source);
            GL.CompileShader(id);
            // Check compile status here: a failed compile otherwise stays silent and only surfaces at link
            // as the uninformative "linking with uncompiled/unspecialized shader" with no file or line.
            GL.GetShader(id, ShaderParameter.CompileStatus, out var status);
            if (status == 0)
                Logger.Error($"Error compiling shader \"{path}\": {GL.GetShaderInfoLog(id)}");
            GL.AttachShader(_programId, id);
        }

        private static string ToAscii(string source, string path)
        {
            for (var i = 0; i < source.Length; i++)
            {
                if (source[i] <= 127) continue;
                Logger.Error($"Shader \"{path}\" contains non-ASCII characters (replaced with '?'); GLSL must be ASCII.");
                var chars = source.ToCharArray();
                for (var j = i; j < chars.Length; j++)
                    if (chars[j] > 127) chars[j] = '?';
                return new string(chars);
            }
            return source;
        }
    }
}
