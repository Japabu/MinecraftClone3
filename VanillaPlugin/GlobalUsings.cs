// Engine math aliases over Silk.NET.Maths (matching the Silk.NET window/input/WebGPU stack and providing the
// integer vectors block coordinates need). These aliases give the familiar type names (Vector3, Vector3i,
// Matrix4, ...); the static helper classes (Vector3D.Dot, Matrix4X4.CreateTranslation, Scalar.Clamp) come from
// `using Silk.NET.Maths`. Colours are Vector4D<float> and math goes through System.MathF / Silk.NET.Maths.Scalar.
global using Silk.NET.Maths;
global using Vector2 = Silk.NET.Maths.Vector2D<float>;
global using Vector3 = Silk.NET.Maths.Vector3D<float>;
global using Vector2i = Silk.NET.Maths.Vector2D<int>;
global using Vector3i = Silk.NET.Maths.Vector3D<int>;
global using Vector4 = Silk.NET.Maths.Vector4D<float>;
global using Matrix4 = Silk.NET.Maths.Matrix4X4<float>;
global using Matrix3 = Silk.NET.Maths.Matrix3X3<float>;
global using Quaternion = Silk.NET.Maths.Quaternion<float>;
// The engine's pixel rectangle. Silk.NET.Maths also defines a Rectangle (never used here); name the engine's
// as canonical so `using Silk.NET.Maths` doesn't shadow it.
global using Rectangle = MinecraftClone3API.Util.Rectangle;
