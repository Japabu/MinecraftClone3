using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenTK.Mathematics;

namespace MinecraftClone3API.IO
{
    public class CustomJsonConverter : JsonConverter
    {
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value is Vector2 v2)
            {
                writer.WriteValue(new[] {v2.X, v2.Y});
                //writer.WriteRawValue($"[ {v2.X}, {v2.Y} ]");
            }
            else if (value is Vector3 v3)
            {
                writer.WriteValue(new[] { v3.X, v3.Y, v3.Z });
                //writer.WriteRawValue($"[ {v3.X}, {v3.Y}, {v3.Z} ]");
            }
            else if (value is Vector4 v4)
            {
                writer.WriteValue(new[] { v4.X, v4.Y, v4.Z, v4.W });
                //writer.WriteRawValue($"[ {v4.X}, {v4.Y}, {v4.Z}, {v4.W} ]");
            }
            else throw new Exception($"{value} cannot be handled by this converter!");
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (objectType == typeof(Vector2))
            {
                var token = JToken.ReadFrom(reader);
                var floats = token.ToObject<float[]>();

                if (floats.Length != 2)
                    throw new Exception($"\"{token.ToString(Formatting.None)}\" is not a Vector2!", null);

                return new Vector2(floats[0], floats[1]);
            }

            if (objectType == typeof(Vector3))
            {
                var token = JToken.ReadFrom(reader);
                var floats = token.ToObject<float[]>();

                if (floats.Length != 3)
                    throw new Exception($"\"{token.ToString(Formatting.None)}\" is not a Vector3!", null);
                return new Vector3(floats[0], floats[1], floats[2]);
            }

            if (objectType == typeof(Vector4))
            {
                var token = JToken.ReadFrom(reader);
                var floats = token.ToObject<float[]>();

                if (floats.Length != 4)
                    throw new Exception($"\"{token.ToString(Formatting.None)}\" is not a Vector4!", null);
                return new Vector4(floats[0], floats[1], floats[2], floats[3]);
            }

            throw new Exception($"{objectType} cannot be handled by this converter!");
        }

        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(Vector2) || objectType == typeof(Vector3) || objectType == typeof(Vector4);
        }

        private static string GetStringBetween(string a, string b, string value)
        {
            var ai = value.IndexOf(a, StringComparison.Ordinal) + a.Length;
            return value.Substring(ai, value.IndexOf(b, StringComparison.Ordinal) - ai);
        }
    }
}
