namespace SimpleExample
{
    using ClickableTransparentOverlay;
    using System.Threading.Tasks;
    using ImGuiNET;
    using ClickableTransparentOverlay.Win32;
    using System.Numerics;

    internal class SampleOverlay : Overlay
    {
        private bool showWindow = true;
        private bool wantKeepDemoWindow = false;
        private bool drawBackground = true;
        private bool isDrawingUI = true;
        public bool IsDrawingUI { get => isDrawingUI; }

        public SampleOverlay() : base(true)
        {
        }

        protected override Task PostInitialized()
        {
            var io = ImGui.GetIO();
            io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard |
                ImGuiConfigFlags.NavEnableGamepad |
                ImGuiConfigFlags.DockingEnable;
            io.Fonts.Flags |= ImFontAtlasFlags.NoBakedLines;
            const string fontPath = "c:\\Windows\\Fonts\\msyh.ttc";
            if (File.Exists(fontPath))
            {
                ReplaceFont(fontPath, (int)(18.0f * DpiScale), FontGlyphRangeType.ChineseFull);
            }

            return base.PostInitialized();
        }

        protected override void Render()
        {
            if (showWindow)
            {
                if (Utils.IsKeyPressedAndNotTimeout(VK.INSERT))
                {
                    ToggleVisible();
                }
                if (isDrawingUI)
                {
                    ImGui.Begin("TestWindow", ref showWindow);
                    // ImGui.Checkbox("Demo Window", ref wantKeepDemoWindow);
                    ImGui.Checkbox("Demo Window", ref wantKeepDemoWindow);
                    ImGui.Checkbox("Show Watermark", ref drawBackground);
                    if (wantKeepDemoWindow)
                    {
                        ImGui.ShowDemoWindow(ref wantKeepDemoWindow);
                    }
                    // Draw background
                    if (drawBackground)
                    {
                        var io = ImGui.GetIO();
                        var pos = new Vector2(io.DisplaySize.X / 2, 100);
                        // Use APlayerController.ProjectWorldLocationToScreen to get screen pos
                        ImGui.GetBackgroundDrawList().AddText(null, 20 * DpiScale, pos, 0xFFFFFFFF, "Welcome to MyMod");
                    }
                    ImGui.End();
                }
            }
            else
            {
                Close();
            }
        }

        public void ToggleVisible()
        {
            isDrawingUI = !isDrawingUI;
        }
    }
}
