using System;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Silk.NET.WebGPU;
using Silk.NET.Core.Native;

unsafe
{
    var wgpu = WebGPU.GetApi();

    var options = WindowOptions.Default with
    {
        API = GraphicsAPI.None, // no GL/Vulkan context; WebGPU owns the surface
        Size = new Vector2D<int>(1280, 720),
        Title = "MC3 wgpu clear-screen probe",
    };
    var window = Window.Create(options);
    window.Initialize();

    Instance* instance = wgpu.CreateInstance(new InstanceDescriptor());
    Surface* surface = window.CreateWebGPUSurface(wgpu, instance);

    // --- request adapter (synchronous via callback) ---
    Adapter* adapter = null;
    var adapterOpts = new RequestAdapterOptions { CompatibleSurface = surface, PowerPreference = PowerPreference.HighPerformance };
    var adapterCb = PfnRequestAdapterCallback.From((status, a, msg, data) =>
    {
        if (status == RequestAdapterStatus.Success) adapter = a;
        else Console.WriteLine($"adapter failed: {status} {SilkMarshal.PtrToString((nint)msg)}");
    });
    wgpu.InstanceRequestAdapter(instance, in adapterOpts, adapterCb, null);
    if (adapter == null) { Console.WriteLine("no adapter"); return; }

    var props = new AdapterProperties();
    wgpu.AdapterGetProperties(adapter, ref props);
    Console.WriteLine($"adapter backend: {props.BackendType}  ({SilkMarshal.PtrToString((nint)props.Name)})");

    // --- request device ---
    Device* device = null;
    var deviceCb = PfnRequestDeviceCallback.From((status, d, msg, data) =>
    {
        if (status == RequestDeviceStatus.Success) device = d;
        else Console.WriteLine($"device failed: {status} {SilkMarshal.PtrToString((nint)msg)}");
    });
    wgpu.AdapterRequestDevice(adapter, null, deviceCb, null);
    if (device == null) { Console.WriteLine("no device"); return; }

    Queue* queue = wgpu.DeviceGetQueue(device);

    // --- pick a surface format & configure ---
    var caps = new SurfaceCapabilities();
    wgpu.SurfaceGetCapabilities(surface, adapter, ref caps);
    TextureFormat format = caps.Formats[0];
    Console.WriteLine($"surface format: {format}");

    void Configure(int w, int h)
    {
        var config = new SurfaceConfiguration
        {
            Device = device,
            Format = format,
            Usage = TextureUsage.RenderAttachment,
            PresentMode = PresentMode.Fifo,
            Width = (uint)w,
            Height = (uint)h,
            AlphaMode = CompositeAlphaMode.Auto,
        };
        wgpu.SurfaceConfigure(surface, in config);
    }
    Configure(window.FramebufferSize.X, window.FramebufferSize.Y);
    window.FramebufferResize += s => Configure(s.X, s.Y);

    double t = 0;
    window.Render += dt =>
    {
        t += dt;

        var st = new SurfaceTexture();
        wgpu.SurfaceGetCurrentTexture(surface, ref st);
        if (st.Status != SurfaceGetCurrentTextureStatus.Success) return;
        TextureView* view = wgpu.TextureCreateView(st.Texture, null);

        var encoder = wgpu.DeviceCreateCommandEncoder(device, null);
        var color = new RenderPassColorAttachment
        {
            View = view,
            LoadOp = LoadOp.Clear,
            StoreOp = StoreOp.Store,
            ClearValue = new Color(0.1, 0.2 + 0.2 * Math.Sin(t), 0.35, 1.0),
        };
        var rp = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &color };
        var pass = wgpu.CommandEncoderBeginRenderPass(encoder, in rp);
        wgpu.RenderPassEncoderEnd(pass);

        var cmd = wgpu.CommandEncoderFinish(encoder, null);
        wgpu.QueueSubmit(queue, 1, &cmd);
        wgpu.SurfacePresent(surface);

        wgpu.RenderPassEncoderRelease(pass);
        wgpu.CommandBufferRelease(cmd);
        wgpu.CommandEncoderRelease(encoder);
        wgpu.TextureViewRelease(view);
        wgpu.TextureRelease(st.Texture);
    };

    Console.WriteLine("entering render loop (close window to exit)");
    window.Run();
}
