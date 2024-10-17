namespace ClickableTransparentOverlay
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.IO;
    using ImGuiNET;
    using Veldrid;

    /// <summary>
    /// A modified version of Veldrid.ImGui's ImGuiRenderer.
    /// Manages input for ImGui and handles rendering ImGui's DrawLists with Veldrid.
    /// </summary>
    internal sealed class ImGuiController : IDisposable
    {
        private GraphicsDevice _gd;
        private bool _frameBegun;

        // Veldrid objects
        private DeviceBuffer _vertexBuffer;
        private DeviceBuffer _indexBuffer;
        private DeviceBuffer _projMatrixBuffer;
        private Texture _fontTexture;
        private TextureView _fontTextureView;
        private Shader _vertexShader;
        private Shader _fragmentShader;
        private ResourceLayout _layout;
        private ResourceLayout _textureLayout;
        private Pipeline _pipeline;
        private ResourceSet _mainResourceSet;
        private ResourceSet _fontTextureResourceSet;

        private readonly IntPtr _fontAtlasID = (IntPtr)1;

        private int _windowWidth;
        private int _windowHeight;
        private Vector2 _scaleFactor = Vector2.One;

        // Image trackers
        private readonly Dictionary<TextureView, ResourceSetInfo> _setsByView
            = new Dictionary<TextureView, ResourceSetInfo>();
        private readonly Dictionary<Texture, TextureView> _autoViewsByTexture
            = new Dictionary<Texture, TextureView>();
        private readonly Dictionary<IntPtr, ResourceSetInfo> _viewsById = new Dictionary<IntPtr, ResourceSetInfo>();
        private readonly List<IDisposable> _ownedResources = new List<IDisposable>();
        private int _lastAssignedID = 100;

        /// <summary>
        /// Constructs a new ImGuiController.
        /// </summary>
        public ImGuiController(GraphicsDevice gd, int width, int height)
        {
            _gd = gd;
            _windowWidth = width;
            _windowHeight = height;

            ImGui.CreateContext();
            SetPerFrameImGuiData(1f / 60f);
        }

        /// <summary>
        /// Starts the ImGui Controller by creating required resources and first new frame.
        /// </summary>
        public void Start()
        {
            CreateDeviceResources();
            ImGui.NewFrame();
            _frameBegun = true;
        }

        /// <summary>
        /// Updates the controller with new window size information.
        /// </summary>
        /// <param name="width">width of the screen.</param>
        /// <param name="height">height of the screen.</param>
        public void WindowResized(int width, int height)
        {
            _windowWidth = width;
            _windowHeight = height;
        }

        public void CreateDeviceResources()
        {
            ResourceFactory factory = _gd.ResourceFactory;
            _vertexBuffer = factory.CreateBuffer(new BufferDescription(10000, BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            _vertexBuffer.Name = "ImGui.NET Vertex Buffer";
            _indexBuffer = factory.CreateBuffer(new BufferDescription(2000, BufferUsage.IndexBuffer | BufferUsage.Dynamic));
            _indexBuffer.Name = "ImGui.NET Index Buffer";
            RecreateFontDeviceTexture(_gd);

            _projMatrixBuffer = factory.CreateBuffer(new BufferDescription(64, BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _projMatrixBuffer.Name = "ImGui.NET Projection Buffer";

            byte[] vertexShaderBytes = LoadEmbeddedShaderCode(_gd.ResourceFactory, "imgui-vertex", ShaderStages.Vertex);
            byte[] fragmentShaderBytes = LoadEmbeddedShaderCode(_gd.ResourceFactory, "imgui-frag", ShaderStages.Fragment);
            _vertexShader = factory.CreateShader(new ShaderDescription(ShaderStages.Vertex, vertexShaderBytes, "main"));
            _fragmentShader = factory.CreateShader(new ShaderDescription(ShaderStages.Fragment, fragmentShaderBytes, "main"));

            VertexLayoutDescription[] vertexLayouts = new VertexLayoutDescription[]
            {
                new VertexLayoutDescription(
                    new VertexElementDescription("in_position", VertexElementSemantic.Position, VertexElementFormat.Float2),
                    new VertexElementDescription("in_texCoord", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                    new VertexElementDescription("in_color", VertexElementSemantic.Color, VertexElementFormat.Byte4_Norm))
            };

            _layout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("ProjectionMatrixBuffer", ResourceKind.UniformBuffer, ShaderStages.Vertex),
                new ResourceLayoutElementDescription("MainSampler", ResourceKind.Sampler, ShaderStages.Fragment)));
            _textureLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("MainTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment)));

            GraphicsPipelineDescription pd = new GraphicsPipelineDescription(
                BlendStateDescription.SingleAlphaBlend,
                new DepthStencilStateDescription(false, false, ComparisonKind.Always),
                new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.Clockwise, false, true),
                PrimitiveTopology.TriangleList,
                new ShaderSetDescription(vertexLayouts, new[] { _vertexShader, _fragmentShader }),
                new ResourceLayout[] { _layout, _textureLayout },
                _gd.MainSwapchain.Framebuffer.OutputDescription,
                ResourceBindingModel.Default);
            _pipeline = factory.CreateGraphicsPipeline(ref pd);

            _mainResourceSet = factory.CreateResourceSet(new ResourceSetDescription(_layout,
                _projMatrixBuffer,
                _gd.PointSampler));

            _fontTextureResourceSet = factory.CreateResourceSet(new ResourceSetDescription(_textureLayout, _fontTextureView));
        }

        /// <summary>
        /// Gets or creates a handle for a texture to be drawn with ImGui.
        /// E.G. Pass the returned handle to Image() or ImageButton().
        /// </summary>
        public IntPtr GetOrCreateImGuiBinding(ResourceFactory factory, TextureView textureView)
        {
            if (!_setsByView.TryGetValue(textureView, out ResourceSetInfo rsi))
            {
                ResourceSet resourceSet = factory.CreateResourceSet(new ResourceSetDescription(_textureLayout, textureView));
                rsi = new ResourceSetInfo(GetNextImGuiBindingID(), resourceSet);

                _setsByView.Add(textureView, rsi);
                _viewsById.Add(rsi.ImGuiBinding, rsi);
                _ownedResources.Add(resourceSet);
            }

            return rsi.ImGuiBinding;
        }

        /// <summary>
        /// Removes the texture from the resources.
        /// </summary>
        /// <param name="textureView">texture to remove</param>
        public void RemoveImGuiBinding(TextureView textureView)
        {
            if (_setsByView.TryGetValue(textureView, out ResourceSetInfo rsi))
            {
                _setsByView.Remove(textureView);
                _viewsById.Remove(rsi.ImGuiBinding);
                _ownedResources.Remove(rsi.ResourceSet);
                rsi.ResourceSet.Dispose();
            }
        }

        /// <summary>
        /// Removes the texture from the resources.
        /// </summary>
        /// <param name="texture">texture to remove</param>
        public void RemoveImGuiBinding(Texture texture)
        {
            if (_autoViewsByTexture.TryGetValue(texture, out TextureView textureView))
            {
                _autoViewsByTexture.Remove(texture);
                _ownedResources.Remove(textureView);
                RemoveImGuiBinding(textureView);
                textureView.Dispose();
            }
        }

        private IntPtr GetNextImGuiBindingID()
        {
            int newID = _lastAssignedID++;
            return (IntPtr)newID;
        }

        /// <summary>
        /// Gets or creates a handle for a texture to be drawn with ImGui.
        /// Pass the returned handle to Image() or ImageButton().
        /// </summary>
        public IntPtr GetOrCreateImGuiBinding(ResourceFactory factory, Texture texture)
        {
            if (!_autoViewsByTexture.TryGetValue(texture, out TextureView textureView))
            {
                textureView = factory.CreateTextureView(texture);
                _autoViewsByTexture.Add(texture, textureView);
                _ownedResources.Add(textureView);
            }

            return GetOrCreateImGuiBinding(factory, textureView);
        }

        /// <summary>
        /// Retrieves the shader texture binding for the given helper handle.
        /// </summary>
        public ResourceSet GetImageResourceSet(IntPtr imGuiBinding)
        {
            if (!_viewsById.TryGetValue(imGuiBinding, out ResourceSetInfo tvi))
            {
                throw new InvalidOperationException("No registered ImGui binding with id " + imGuiBinding.ToString());
            }

            return tvi.ResourceSet;
        }

        public void ClearCachedImageResources()
        {
            foreach (IDisposable resource in _ownedResources)
            {
                resource.Dispose();
            }

            _ownedResources.Clear();
            _setsByView.Clear();
            _viewsById.Clear();
            _autoViewsByTexture.Clear();
            _lastAssignedID = 100;
        }

        private byte[] LoadEmbeddedShaderCode(ResourceFactory factory, string name, ShaderStages _)
        {
            switch (factory.BackendType)
            {
                case GraphicsBackend.Direct3D11:
                    {
                        string resourceName = name + ".hlsl";
                        return GetEmbeddedResourceBytes(resourceName);
                    }
                default:
                    throw new NotImplementedException();
            }
        }

        private byte[] GetEmbeddedResourceBytes(string resourceName)
        {
            Assembly assembly = typeof(ImGuiController).Assembly;
            using (Stream s = assembly.GetManifestResourceStream(resourceName))
            {
                byte[] ret = new byte[s.Length];
                s.Read(ret, 0, (int)s.Length);
                return ret;
            }
        }

        /// <summary>
        /// Recreates the device texture used to render text.
        /// </summary>
        public void RecreateFontDeviceTexture(GraphicsDevice gd)
        {
            ImGuiIOPtr io = ImGui.GetIO();
            // Build
            io.Fonts.GetTexDataAsRGBA32(
                out IntPtr pixels,
                out int width,
                out int height,
                out int bytesPerPixel);
            // Store our identifier
            io.Fonts.SetTexID(_fontAtlasID);

            _fontTexture = gd.ResourceFactory.CreateTexture(TextureDescription.Texture2D(
                (uint)width,
                (uint)height,
                1,
                1,
                PixelFormat.R8_G8_B8_A8_UNorm,
                TextureUsage.Sampled));
            _fontTexture.Name = "ImGui.NET Font Texture";
            gd.UpdateTexture(
                _fontTexture,
                pixels,
                (uint)(bytesPerPixel * width * height),
                0,
                0,
                0,
                (uint)width,
                (uint)height,
                1,
                0,
                0);
            _fontTextureView = gd.ResourceFactory.CreateTextureView(_fontTexture);

            io.Fonts.ClearTexData();
        }

        /// <summary>
        /// Renders the ImGui draw list data.
        /// This method requires a <see cref="GraphicsDevice"/> because it may create new DeviceBuffers if the size of vertex
        /// or index data has increased beyond the capacity of the existing buffers.
        /// A <see cref="CommandList"/> is needed to submit drawing and resource update commands.
        /// </summary>
        public void Render(GraphicsDevice gd, CommandList cl)
        {
            if (_frameBegun)
            {
                _frameBegun = false;
                ImGui.Render();
                RenderImDrawData(ImGui.GetDrawData(), gd, cl);
            }
        }

        /// <summary>
        /// Updates ImGui input and IO configuration state.
        /// </summary>
        public void Update(float deltaSeconds, InputSnapshot snapshot, IntPtr handle)
        {
            if (_frameBegun)
            {
                ImGui.Render();
            }

            SetPerFrameImGuiData(deltaSeconds);
            UpdateImGuiInput(snapshot, handle);

            _frameBegun = true;
            ImGui.NewFrame();
        }

        /// <summary>
        /// Sets per-frame data based on the associated window.
        /// This is called by Update(float).
        /// </summary>
        private void SetPerFrameImGuiData(float deltaSeconds)
        {
            ImGuiIOPtr io = ImGui.GetIO();
            io.DisplaySize = new Vector2(
                _windowWidth / _scaleFactor.X,
                _windowHeight / _scaleFactor.Y);
            io.DisplayFramebufferScale = _scaleFactor;
            io.DeltaTime = deltaSeconds; // DeltaTime is in seconds.
        }

        private bool TryMapKey(Key key, out ImGuiKey result)
        {
            ImGuiKey KeyToImGuiKeyShortcut(Key keyToConvert, Key startKey1, ImGuiKey startKey2)
            {
                int changeFromStart1 = (int)keyToConvert - (int)startKey1;
                return startKey2 + changeFromStart1;
            }

            result = ImGuiKey.None; // 默认值设为 ImGuiKey.None
            if (key >= Key.F1 && key <= Key.F24)
            {
                result = KeyToImGuiKeyShortcut(key, Key.F1, ImGuiKey.F1);
            }
            else if (key >= Key.Keypad0 && key <= Key.Keypad9)
            {
                result = KeyToImGuiKeyShortcut(key, Key.Keypad0, ImGuiKey.Keypad0);
            }
            else if (key >= Key.A && key <= Key.Z)
            {
                result = KeyToImGuiKeyShortcut(key, Key.A, ImGuiKey.A);
            }
            else if (key >= Key.Number0 && key <= Key.Number9)
            {
                result = KeyToImGuiKeyShortcut(key, Key.Number0, ImGuiKey._0);
            }
            else
            {
                switch (key)
                {
                    case Key.ShiftLeft:
                    case Key.ShiftRight:
                        result = ImGuiKey.ModShift;
                        break;
                    case Key.ControlLeft:
                    case Key.ControlRight:
                        result = ImGuiKey.ModCtrl;
                        break;
                    case Key.AltLeft:
                    case Key.AltRight:
                        result = ImGuiKey.ModAlt;
                        break;
                    case Key.WinLeft:
                    case Key.WinRight:
                        result = ImGuiKey.ModSuper;
                        break;
                    case Key.Menu:
                        result = ImGuiKey.Menu;
                        break;
                    case Key.Up:
                        result = ImGuiKey.UpArrow;
                        break;
                    case Key.Down:
                        result = ImGuiKey.DownArrow;
                        break;
                    case Key.Left:
                        result = ImGuiKey.LeftArrow;
                        break;
                    case Key.Right:
                        result = ImGuiKey.RightArrow;
                        break;
                    case Key.Enter:
                        result = ImGuiKey.Enter;
                        break;
                    case Key.Escape:
                        result = ImGuiKey.Escape;
                        break;
                    case Key.Space:
                        result = ImGuiKey.Space;
                        break;
                    case Key.Tab:
                        result = ImGuiKey.Tab;
                        break;
                    case Key.BackSpace:
                        result = ImGuiKey.Backspace;
                        break;
                    case Key.Insert:
                        result = ImGuiKey.Insert;
                        break;
                    case Key.Delete:
                        result = ImGuiKey.Delete;
                        break;
                    case Key.PageUp:
                        result = ImGuiKey.PageUp;
                        break;
                    case Key.PageDown:
                        result = ImGuiKey.PageDown;
                        break;
                    case Key.Home:
                        result = ImGuiKey.Home;
                        break;
                    case Key.End:
                        result = ImGuiKey.End;
                        break;
                    case Key.CapsLock:
                        result = ImGuiKey.CapsLock;
                        break;
                    case Key.ScrollLock:
                        result = ImGuiKey.ScrollLock;
                        break;
                    case Key.PrintScreen:
                        result = ImGuiKey.PrintScreen;
                        break;
                    case Key.Pause:
                        result = ImGuiKey.Pause;
                        break;
                    case Key.NumLock:
                        result = ImGuiKey.NumLock;
                        break;
                    case Key.KeypadDivide:
                        result = ImGuiKey.KeypadDivide;
                        break;
                    case Key.KeypadMultiply:
                        result = ImGuiKey.KeypadMultiply;
                        break;
                    case Key.KeypadSubtract:
                        result = ImGuiKey.KeypadSubtract;
                        break;
                    case Key.KeypadAdd:
                        result = ImGuiKey.KeypadAdd;
                        break;
                    case Key.KeypadDecimal:
                        result = ImGuiKey.KeypadDecimal;
                        break;
                    case Key.KeypadEnter:
                        result = ImGuiKey.KeypadEnter;
                        break;
                    case Key.Tilde:
                        result = ImGuiKey.GraveAccent;
                        break;
                    case Key.Minus:
                        result = ImGuiKey.Minus;
                        break;
                    case Key.Plus:
                        result = ImGuiKey.Equal;
                        break;
                    case Key.BracketLeft:
                        result = ImGuiKey.LeftBracket;
                        break;
                    case Key.BracketRight:
                        result = ImGuiKey.RightBracket;
                        break;
                    case Key.Semicolon:
                        result = ImGuiKey.Semicolon;
                        break;
                    case Key.Quote:
                        result = ImGuiKey.Apostrophe;
                        break;
                    case Key.Comma:
                        result = ImGuiKey.Comma;
                        break;
                    case Key.Period:
                        result = ImGuiKey.Period;
                        break;
                    case Key.Slash:
                        result = ImGuiKey.Slash;
                        break;
                    case Key.BackSlash:
                    case Key.NonUSBackSlash:
                        result = ImGuiKey.Backslash;
                        break;
                }
            }

            return result != ImGuiKey.None;
        }

        private void UpdateImGuiInput(InputSnapshot snapshot, IntPtr handle)
        {
            ImGuiIOPtr io = ImGui.GetIO();

            // Determine if any of the mouse buttons were pressed during this snapshot period, even if they are no longer held.
            bool leftPressed = false;
            bool middlePressed = false;
            bool rightPressed = false;
            for (int i = 0; i < snapshot.MouseEvents.Count; i++)
            {
                MouseEvent me = snapshot.MouseEvents[i];
                if (me.Down)
                {
                    switch (me.MouseButton)
                    {
                        case MouseButton.Left:
                            leftPressed = true;
                            break;
                        case MouseButton.Middle:
                            middlePressed = true;
                            break;
                        case MouseButton.Right:
                            rightPressed = true;
                            break;
                    }
                }
            }

            if (NativeMethods.IsClickable)
            {
                leftPressed |= snapshot.IsMouseDown(MouseButton.Left);
                rightPressed |= snapshot.IsMouseDown(MouseButton.Right);
                middlePressed |= snapshot.IsMouseDown(MouseButton.Middle);
            }

            io.MouseDown[0] = leftPressed;
            io.MouseDown[1] = rightPressed;
            io.MouseDown[2] = middlePressed;
            io.MousePos = NativeMethods.GetCursorPosition(handle);
            io.MouseWheel = snapshot.WheelDelta;

            IReadOnlyList<char> keyCharPresses = snapshot.KeyCharPresses;
            for (int i = 0; i < keyCharPresses.Count; i++)
            {
                char c = keyCharPresses[i];
                io.AddInputCharacter(c);
            }

            for (int i = 0; i < snapshot.KeyEvents.Count; i++)
            {
                KeyEvent keyEvent = snapshot.KeyEvents[i];
                if (TryMapKey(keyEvent.Key, out ImGuiKey imguikey))
                {
                    io.AddKeyEvent(imguikey, keyEvent.Down);
                }
            }

            if (io.WantCaptureKeyboard || io.WantCaptureMouse)
            {
                NativeMethods.SetOverlayClickable(handle, true);
            }
            else
            {
                NativeMethods.SetOverlayClickable(handle, false);
            }
        }

        private void RenderImDrawData(ImDrawDataPtr draw_data, GraphicsDevice gd, CommandList cl)
        {
            uint vertexOffsetInVertices = 0;
            uint indexOffsetInElements = 0;

            if (draw_data.CmdListsCount == 0)
            {
                return;
            }

            uint totalVBSize = (uint)(draw_data.TotalVtxCount * Unsafe.SizeOf<ImDrawVert>());
            if (totalVBSize > _vertexBuffer.SizeInBytes)
            {
                _vertexBuffer.Dispose();
                _vertexBuffer = gd.ResourceFactory.CreateBuffer(new BufferDescription((uint)(totalVBSize * 1.5f), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            }

            uint totalIBSize = (uint)(draw_data.TotalIdxCount * sizeof(ushort));
            if (totalIBSize > _indexBuffer.SizeInBytes)
            {
                _indexBuffer.Dispose();
                _indexBuffer = gd.ResourceFactory.CreateBuffer(new BufferDescription((uint)(totalIBSize * 1.5f), BufferUsage.IndexBuffer | BufferUsage.Dynamic));
            }

            for (int i = 0; i < draw_data.CmdListsCount; i++)
            {
                ImDrawListPtr cmd_list = draw_data.CmdLists[i];

                cl.UpdateBuffer(
                    _vertexBuffer,
                    vertexOffsetInVertices * (uint)Unsafe.SizeOf<ImDrawVert>(),
                    cmd_list.VtxBuffer.Data,
                    (uint)(cmd_list.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>()));

                cl.UpdateBuffer(
                    _indexBuffer,
                    indexOffsetInElements * sizeof(ushort),
                    cmd_list.IdxBuffer.Data,
                    (uint)(cmd_list.IdxBuffer.Size * sizeof(ushort)));

                vertexOffsetInVertices += (uint)cmd_list.VtxBuffer.Size;
                indexOffsetInElements += (uint)cmd_list.IdxBuffer.Size;
            }

            // Setup orthographic projection matrix into our constant buffer
            ImGuiIOPtr io = ImGui.GetIO();
            Matrix4x4 mvp = Matrix4x4.CreateOrthographicOffCenter(
                0f,
                io.DisplaySize.X,
                io.DisplaySize.Y,
                0.0f,
                -1.0f,
                1.0f);

            _gd.UpdateBuffer(_projMatrixBuffer, 0, ref mvp);

            cl.SetVertexBuffer(0, _vertexBuffer);
            cl.SetIndexBuffer(_indexBuffer, IndexFormat.UInt16);
            cl.SetPipeline(_pipeline);
            cl.SetGraphicsResourceSet(0, _mainResourceSet);

            draw_data.ScaleClipRects(io.DisplayFramebufferScale);

            // Render command lists
            int vtx_offset = 0;
            int idx_offset = 0;
            for (int n = 0; n < draw_data.CmdListsCount; n++)
            {
                ImDrawListPtr cmd_list = draw_data.CmdLists[n];
                for (int cmd_i = 0; cmd_i < cmd_list.CmdBuffer.Size; cmd_i++)
                {
                    ImDrawCmdPtr pcmd = cmd_list.CmdBuffer[cmd_i];
                    if (pcmd.UserCallback != IntPtr.Zero)
                    {
                        throw new NotImplementedException();
                    }
                    else
                    {
                        if (pcmd.TextureId != IntPtr.Zero)
                        {
                            if (pcmd.TextureId == _fontAtlasID)
                            {
                                cl.SetGraphicsResourceSet(1, _fontTextureResourceSet);
                            }
                            else
                            {
                                cl.SetGraphicsResourceSet(1, GetImageResourceSet(pcmd.TextureId));
                            }
                        }

                        cl.SetScissorRect(
                            0,
                            (uint)pcmd.ClipRect.X,
                            (uint)pcmd.ClipRect.Y,
                            (uint)(pcmd.ClipRect.Z - pcmd.ClipRect.X),
                            (uint)(pcmd.ClipRect.W - pcmd.ClipRect.Y));

                        cl.DrawIndexed(pcmd.ElemCount, 1, (uint)idx_offset, vtx_offset, 0);
                    }

                    idx_offset += (int)pcmd.ElemCount;
                }
                vtx_offset += cmd_list.VtxBuffer.Size;
            }
        }

        /// <summary>
        /// Frees all graphics resources used by the renderer.
        /// </summary>
        public void Dispose()
        {
            _vertexBuffer.Dispose();
            _indexBuffer.Dispose();
            _projMatrixBuffer.Dispose();
            _fontTexture.Dispose();
            _fontTextureView.Dispose();
            _vertexShader.Dispose();
            _fragmentShader.Dispose();
            _layout.Dispose();
            _textureLayout.Dispose();
            _pipeline.Dispose();
            _mainResourceSet.Dispose();
            ClearCachedImageResources();
            _fontTextureResourceSet.Dispose();
        }

        private struct ResourceSetInfo
        {
            public readonly IntPtr ImGuiBinding;
            public readonly ResourceSet ResourceSet;

            public ResourceSetInfo(IntPtr imGuiBinding, ResourceSet resourceSet)
            {
                ImGuiBinding = imGuiBinding;
                ResourceSet = resourceSet;
            }
        }
    }
}
