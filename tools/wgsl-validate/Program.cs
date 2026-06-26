using System;
using System.IO;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using Silk.NET.WebGPU.Extensions.WGPU;

unsafe
{
    if (args.Length < 1) { Console.WriteLine("usage: wgsl-validate <shaderDir>"); Environment.Exit(2); }
    var dir = args[0];
    if (!Directory.Exists(dir)) { Console.WriteLine($"no such dir: {dir}"); Environment.Exit(2); }

    var wgpu = WebGPU.GetApi();
    var instance = wgpu.CreateInstance(new InstanceDescriptor());

    // Headless: no CompatibleSurface. wgpu-native still returns a hardware adapter (Metal on macOS).
    Adapter* adapter = null;
    var adapterOpts = new RequestAdapterOptions { PowerPreference = PowerPreference.HighPerformance };
    wgpu.InstanceRequestAdapter(instance, in adapterOpts,
        PfnRequestAdapterCallback.From((status, a, msg, _) =>
        {
            if (status == RequestAdapterStatus.Success) adapter = a;
            else Console.WriteLine($"adapter failed: {status} {SilkMarshal.PtrToString((nint)msg)}");
        }), null);
    if (adapter == null) { Console.WriteLine("no adapter"); Environment.Exit(1); }

    Device* device = null;
    // Enable the native features our shaders use (push constants) so they validate as they will in-engine.
    var features = stackalloc FeatureName[] { (FeatureName)NativeFeature.PushConstants };
    var deviceDesc = new DeviceDescriptor { RequiredFeatureCount = 1, RequiredFeatures = features };
    wgpu.AdapterRequestDevice(adapter, in deviceDesc,
        PfnRequestDeviceCallback.From((status, d, msg, _) =>
        {
            if (status == RequestDeviceStatus.Success) device = d;
            else Console.WriteLine($"device failed: {status} {SilkMarshal.PtrToString((nint)msg)}");
        }), null);
    if (device == null) { Console.WriteLine("no device"); Environment.Exit(1); }

    var failures = 0;
    var errored = false;
    wgpu.DeviceSetUncapturedErrorCallback(device,
        PfnErrorCallback.From((type, msg, _) =>
        {
            errored = true;
            Console.WriteLine($"  [{type}] {SilkMarshal.PtrToString((nint)msg)}");
        }), null);

    var files = Directory.GetFiles(dir, "*.wgsl", SearchOption.AllDirectories);
    Array.Sort(files);
    foreach (var file in files)
    {
        var src = File.ReadAllText(file);
        var name = Path.GetFileName(file);
        errored = false;

        var codePtr = (byte*)SilkMarshal.StringToPtr(src, NativeStringEncoding.UTF8);
        var wgslDesc = new ShaderModuleWGSLDescriptor
        {
            Chain = new ChainedStruct { SType = SType.ShaderModuleWgslDescriptor },
            Code = codePtr,
        };
        var desc = new ShaderModuleDescriptor { NextInChain = (ChainedStruct*)&wgslDesc };

        var module = wgpu.DeviceCreateShaderModule(device, in desc);

        if (module == null || errored)
        {
            failures++;
            Console.WriteLine($"FAIL  {name}");
        }
        else
        {
            Console.WriteLine($"ok    {name}");
        }

        if (module != null) wgpu.ShaderModuleRelease(module);
        SilkMarshal.Free((nint)codePtr);
    }

    Console.WriteLine($"\n{files.Length - failures}/{files.Length} shaders valid");
    Environment.Exit(failures == 0 ? 0 : 1);
}
