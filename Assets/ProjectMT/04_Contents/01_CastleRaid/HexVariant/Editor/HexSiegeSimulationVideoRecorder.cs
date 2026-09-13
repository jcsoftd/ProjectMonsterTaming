using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace ProjectMT.Contents.CastleRaidHex.Editor
{
    // Optional observation capture only. Recorded runs are excluded from performance baselines.
    internal static class HexSiegeSimulationVideoRecorder
    {
        private static System.Diagnostics.Process encoder;
        private static RenderTexture render;
        private static Texture2D pixels;
        private static MethodInfo capture;
        private static EditorWindow window;
        private static Rect previousWindowPosition;
        private static string videoPath;
        private static int everyTicks;

        internal static void Begin(HexSiegeSimulationResult result)
        {
            if (!result.scenario.recordVideo) return;
            var binary = Path.GetFullPath(Path.Combine(HexSiegeSimulationData.OutputDirectory,
                "../siege-media-tools/node_modules/ffmpeg-static/ffmpeg.exe"));
            if (!File.Exists(binary)) throw new FileNotFoundException("영상 인코더가 없습니다. siege-media-tools의 ffmpeg-static을 준비하세요.", binary);
            capture = typeof(InternalEditorUtility).GetMethod("CaptureEditorWindow",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(EditorWindow), typeof(RenderTexture) }, null);
            if (capture == null) throw new NotSupportedException("이 Unity는 Editor 창 직접 캡처를 지원하지 않습니다.");
            var gameView = SessionState.GetBool("HexSiegeSimulator.RecordGameView", false);
            SessionState.EraseBool("HexSiegeSimulator.RecordGameView"); // 한 번의 촬영에만 적용한다.
            var gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            if (gameView && gameViewType == null) throw new NotSupportedException("GameView를 찾지 못했습니다.");
            window = gameView ? EditorWindow.GetWindow(gameViewType) :
                EditorWindow.GetWindow<HexSiegeSimulationWindow>("공성 AI 시뮬레이터");
            previousWindowPosition = window.position;
            window.position = new Rect(30, 30, 1920, 1080); window.Show();
            render = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
            pixels = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
            var directory = Path.Combine(HexSiegeSimulationData.OutputDirectory, "videos"); Directory.CreateDirectory(directory);
            videoPath = Path.Combine(directory, (gameView ? "gameview-" : "") + result.scenario.name + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".mp4");
            everyTicks = Math.Max(1, Mathf.RoundToInt(.1f / result.scenario.step));
            var rate = (1f / (result.scenario.step * everyTicks)).ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
            encoder = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = binary,
                Arguments = "-hide_banner -loglevel error -f image2pipe -framerate " + rate +
                    " -vcodec png -i pipe:0 -c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -movflags +faststart \"" + videoPath + "\"",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardError = true
            } };
            if (!encoder.Start()) throw new InvalidOperationException("영상 인코더 시작 실패");
            encoder.BeginErrorReadLine();
            result.videoPath = videoPath;
        }

        internal static void Capture(HexSiegeSimulationResult result, int tick)
        {
            if (encoder == null || tick % everyTicks != 0) return;
            if (window is HexSiegeSimulationWindow simulator) simulator.PrepareVideoFrame(result, tick);
            if (!(capture.Invoke(null, new object[] { window, render }) is bool ok) || !ok)
                throw new InvalidOperationException("Unity 창 영상 프레임 캡처 실패");
            var old = RenderTexture.active;
            try
            {
                RenderTexture.active = render;
                pixels.ReadPixels(new Rect(0, 0, render.width, render.height), 0, 0); pixels.Apply();
                var png = pixels.EncodeToPNG();
                encoder.StandardInput.BaseStream.Write(png, 0, png.Length);
            }
            finally { RenderTexture.active = old; }
        }

        internal static void Finish()
        {
            try
            {
                if (encoder == null) return;
                encoder.StandardInput.Close();
                if (!encoder.WaitForExit(10000)) { encoder.Kill(); throw new TimeoutException("영상 인코딩 종료 시간 초과"); }
                if (encoder.ExitCode != 0) throw new IOException("영상 인코딩 실패: " + videoPath);
            }
            finally
            {
                encoder?.Dispose(); encoder = null;
                if (render != null) { render.Release(); UnityEngine.Object.DestroyImmediate(render); }
                if (pixels != null) UnityEngine.Object.DestroyImmediate(pixels);
                if (window != null) window.position = previousWindowPosition;
                render = null; pixels = null; window = null;
            }
        }
    }
}
