using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Silk.NET.Maths;

namespace MinecraftClone3API.IO
{
    public class CustomJsonConverter : JsonConverter
    {
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value is Vector2D<float> v2)
            {
                writer.WriteValue(new[] {v2.X, v2.Y});
                //writer.WriteRawValue($"[ {v2.X}, {v2.Y} ]");
            }
            else if (value is Vector3D<float> v3)
            {
                writer.WriteValue(new[] { v3.X, v3.Y, v3.Z });
                //writer.WriteRawValue($"[ {v3.X}, {v3.Y}, {v3.Z} ]");
            }
            else if (value is Vector4D<float> v4)
            {
                writer.WriteValue(new[] { v4.X, v4.Y, v4.Z, v4.W });
                //writer.WriteRawValue($"[ {v4.X}, {v4.Y}, {v4.Z}, {v4.W} ]");
            }
            else throw new Exception($"{value} cannot be handled by this converter!");
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (objectType == typeof(Vector2D<float>))
            {
                var token = JToken.ReadFrom(reader);
                var floats = token.ToObject<float[]>();

                if (floats.Length != 2)
                    throw new Exception($"\"{token.ToString(Formatting.None)}\" is not a Vector2!", null);

                return new Vector2D<float>(floats[0], floats[1]);
            }

            if (objectType == typeof(Vector3D<float>))
            {
                var token = JToken.ReadFrom(reader);
                var floats = token.ToObject<float[]>();

                if (floats.Length != 3)
                    throw new Exception($"\"{token.ToString(Formatting.None)}\" is not a Vector3!", null);
                return new Vector3D<float>(floats[0], floats[1], floats[2]);
            }

            if (objectType == typeof(Vector4D<float>))
            {
                var token = JToken.ReadFrom(reader);
                var floats = token.ToObject<float[]>();

                if (floats.Length != 4)
                    throw new Exception($"\"{token.ToString(Formatting.None)}\" is not a Vector4!", null);
                return new Vector4D<float>(floats[0], floats[1], floats[2], floats[3]);
            }

            throw new Exception($"{objectType} cannot be handled by this converter!");
        }

        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(Vector2D<float>) || objectType == typeof(Vector3D<float>) || objectType == typeof(Vector4D<float>);
        }

        private static string GetStringBetween(string a, string b, string value)
        {
            var ai = value.IndexOf(a, StringComparison.Ordinal) + a.Length;
            return value.Substring(ai, value.IndexOf(b, StringComparison.Ordinal) - ai);
        }
    }
}
