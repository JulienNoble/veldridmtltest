using System;
using System.Text;
using AppKit;
using Foundation;
using OpenTK;
using Veldrid;

namespace veldridMTLOSX
{
    public partial class ViewController : NSViewController
    {
        private bool viewLoaded;
        private NSTimer displayTimer;

        private uint width => (uint)View.Frame.Width;
        private uint height => (uint)View.Frame.Height;

        private GraphicsDevice graphicsDevice;
        private Swapchain swapchain;
        private ResourceFactory factory;

        private Texture renderTargetColorTexture;
        private TextureView renderTargetColorTextureView;
        private Texture renderTargetDepthTexture;
        private Framebuffer renderTargetFramebuffer;

        private Pipeline pipeline;
        private ResourceLayout resourceLayout;
        private ResourceSet resourceSet;

        private CommandList commandList;

        public ViewController(IntPtr handle) : base(handle)
        {
        }

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();

            // device init
            GraphicsDeviceOptions options = new GraphicsDeviceOptions(false, null, false, ResourceBindingModel.Improved);
#if DEBUG
            options.Debug = true;
#endif
            SwapchainSource ss = SwapchainSource.CreateNSView(this.View.Handle);
            SwapchainDescription scd = new SwapchainDescription(
                ss,
                width, height,
                PixelFormat.R32_Float,
                false);

            graphicsDevice = GraphicsDevice.CreateMetal(options);
            swapchain = graphicsDevice.ResourceFactory.CreateSwapchain(ref scd);
            factory = graphicsDevice.ResourceFactory;

            // resource init
            CreateSizeDependentResources();
            VertexPosition[] quadVertices =
            {
                new VertexPosition(new Vector3 (-1, 1, 0)),
                new VertexPosition(new Vector3 (1, 1, 0)),
                new VertexPosition(new Vector3 (-1, -1, 0)),
                new VertexPosition(new Vector3 (1, -1, 0))
            };
            uint[] quadIndices = new uint[]
            {
                0,
                1,
                2,
                1,
                3,
                2
            };
            vertexBuffer = factory.CreateBuffer(new BufferDescription(4 * VertexPosition.SizeInBytes, BufferUsage.VertexBuffer));
            indexBuffer = factory.CreateBuffer(new BufferDescription(6 * sizeof(uint), BufferUsage.IndexBuffer));
            graphicsDevice.UpdateBuffer(vertexBuffer, 0, quadVertices);
            graphicsDevice.UpdateBuffer(indexBuffer, 0, quadIndices);

            commandList = factory.CreateCommandList();

            viewLoaded = true;

            displayTimer = NSTimer.CreateRepeatingTimer(60.0 / 1000.0, Render);
            displayTimer.Fire();
        }

        private void Render(NSTimer timer)
        {
            if (viewLoaded)
            {
                commandList.Begin();
                commandList.SetFramebuffer(renderTargetFramebuffer);
                commandList.ClearColorTarget(0, RgbaFloat.Blue);
                commandList.End();
                graphicsDevice.SubmitCommands(commandList);
                graphicsDevice.WaitForIdle();

                commandList.Begin();
                commandList.SetFramebuffer(swapchain.Framebuffer);
                commandList.ClearColorTarget(0, RgbaFloat.Green);

                commandList.SetPipeline(pipeline);
                commandList.SetVertexBuffer(0, vertexBuffer);
                commandList.SetIndexBuffer(indexBuffer, IndexFormat.UInt32);
                commandList.SetGraphicsResourceSet(0, resourceSet);

                commandList.DrawIndexed(
                    indexCount: 6,
                    instanceCount: 1,
                    indexStart: 0,
                    vertexOffset: 0,
                    instanceStart: 0);

                commandList.End();
                graphicsDevice.SubmitCommands(commandList);
                graphicsDevice.WaitForIdle();
                graphicsDevice.SwapBuffers(swapchain);
            }
        }

        public override void ViewDidLayout()
        {
            base.ViewDidLayout();

            resourceSet.Dispose();
            resourceLayout.Dispose();
            pipeline.Dispose();
            renderTargetFramebuffer.Dispose();
            renderTargetDepthTexture.Dispose();
            renderTargetColorTextureView.Dispose();
            renderTargetColorTexture.Dispose();

            swapchain.Resize(width, height);
            CreateSizeDependentResources();
        }

        public override NSObject RepresentedObject
        {
            get
            {
                return base.RepresentedObject;
            }
            set
            {
                base.RepresentedObject = value;
                // Update the view, if already loaded.
            }
        }

        private void CreateSizeDependentResources()
        {
            renderTargetColorTexture = factory.CreateTexture(TextureDescription.Texture2D(
                width, height, 1, 1,
                PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.RenderTarget | TextureUsage.Sampled));
            renderTargetColorTextureView = factory.CreateTextureView(renderTargetColorTexture);
            renderTargetDepthTexture = factory.CreateTexture(TextureDescription.Texture2D(
                width, height, 1, 1, PixelFormat.R32_Float, TextureUsage.DepthStencil));
            renderTargetFramebuffer = factory.CreateFramebuffer(new FramebufferDescription(renderTargetDepthTexture, renderTargetColorTexture));

            // final render pipeline
            ResourceLayoutElementDescription[] layoutDescriptions = new ResourceLayoutElementDescription[]
            {
                new ResourceLayoutElementDescription ("ScreenMap", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription ("ScreenMapSampler", ResourceKind.Sampler, ShaderStages.Fragment),
            };
            BindableResource[] bindableResources = new BindableResource[]
            {
                renderTargetColorTextureView,
                graphicsDevice.PointSampler,
            };
            resourceLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(layoutDescriptions));
            pipeline = factory.CreateGraphicsPipeline(
                new GraphicsPipelineDescription(
                    BlendStateDescription.SingleOverrideBlend,
                    new DepthStencilStateDescription(
                        depthTestEnabled: false,
                        depthWriteEnabled: false,
                        comparisonKind: ComparisonKind.Always),
                    new RasterizerStateDescription(
                        cullMode: FaceCullMode.Back,
                        fillMode: PolygonFillMode.Solid,
                        frontFace: FrontFace.Clockwise,
                        depthClipEnabled: false,
                        scissorTestEnabled: false),
                    PrimitiveTopology.TriangleList,
                    new ShaderSetDescription(
                        vertexLayouts: new VertexLayoutDescription[] {
                            new VertexLayoutDescription(
                                new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3)
                            )
                        },
                        shaders: CreateShaders(finalVertexCode, finalFragmentCode)),
                    new ResourceLayout[] { resourceLayout },
                    swapchain.Framebuffer.OutputDescription)
            );
            resourceSet = factory.CreateResourceSet(new ResourceSetDescription(resourceLayout, bindableResources));
        }

        private Shader[] CreateShaders(string vertexCode, string fragmentCode)
        {
            ShaderDescription vertexShaderDesc = CreateShaderDescription(vertexCode, ShaderStages.Vertex);
            ShaderDescription fragmentShaderDesc = CreateShaderDescription(fragmentCode, ShaderStages.Fragment);

            return new Shader[] {
                        factory.CreateShader(ref vertexShaderDesc),
                        factory.CreateShader(ref fragmentShaderDesc)
                        };
        }

        private ShaderDescription CreateShaderDescription(string code, ShaderStages stage)
        {
            byte[] data = Encoding.UTF8.GetBytes(code);
            return new ShaderDescription(stage, data, "main0");
        }

        public struct VertexPosition
        {
            public Vector3 Position;

            public const uint SizeInBytes = 12;

            public VertexPosition(Vector3 position)
            {
                Position = position;
            }
        }

        private string finalVertexCode = @"
#include <metal_stdlib>
#include <simd/simd.h>

using namespace metal;

struct main0_out
{
    float3 frag_Position [[user(locn0)]];
    float4 gl_Position [[position]];
};

struct main0_in
{
    float3 Position [[attribute(0)]];
};

vertex main0_out main0(main0_in in [[stage_in]])
{
    main0_out out = {};
    out.gl_Position = float4(in.Position, 1.0);
    out.frag_Position = in.Position;
    return out;
}";
        private string finalFragmentCode = @"#include <metal_stdlib>
#include <simd/simd.h>

using namespace metal;

struct main0_out
{
    float4 outputColor [[color(0)]];
};

struct main0_in
{
    float3 frag_Position [[user(locn0)]];
};

fragment main0_out main0(main0_in in [[stage_in]], texture2d<float> ScreenMap [[texture(0)]], sampler ScreenMapSampler [[sampler(0)]])
{
    main0_out out = {};
    float2 screenSpaceUV = float2((in.frag_Position.x + 1.0) / 2.0, (-(in.frag_Position.y + 1.0)) / 2.0);
    float4 color = ScreenMap.sample(ScreenMapSampler, screenSpaceUV);
    out.outputColor = color;
    return out;
}";
        private DeviceBuffer vertexBuffer;
        private DeviceBuffer indexBuffer;
    }
}
