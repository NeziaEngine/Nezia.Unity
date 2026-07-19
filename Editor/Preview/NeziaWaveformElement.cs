using UnityEngine;
using UnityEngine.UIElements;

namespace Nezia.Unity.Editor.Preview
{
    /// <summary>
    /// IP-6: import 時に焼き込んだ波形ピーク (<see cref="NeziaAudioClip.waveformPeaks"/>)
    /// を描画する UI Toolkit 要素。<see cref="Painter2D"/> による上下対称の
    /// ピークシルエットで、Editor では PCM デコードを一切行わない
    /// (integration-experience.md IP-6 の方針)。
    /// </summary>
    internal sealed class NeziaWaveformElement : VisualElement
    {
        /// <summary>波形の描画色 (Unity Editor の選択色に近いブルー)。</summary>
        private static readonly Color WaveColor = new(0.29f, 0.56f, 0.85f, 0.9f);

        /// <summary>中央線の色。</summary>
        private static readonly Color CenterColor = new(1f, 1f, 1f, 0.12f);

        /// <summary>ピークが小さくても見えるようにする最小振幅 (高さ比)。</summary>
        private const float MinAmplitude = 0.01f;

        private readonly float[] _peaks;

        public NeziaWaveformElement(float[] peaks)
        {
            _peaks = peaks;
            AddToClassList("preview__waveform");
            generateVisualContent += OnGenerateVisualContent;
        }

        private void OnGenerateVisualContent(MeshGenerationContext ctx)
        {
            var rect = contentRect;
            if (_peaks == null || _peaks.Length == 0 || rect.width <= 2f || rect.height <= 2f)
            {
                return;
            }

            var painter = ctx.painter2D;
            var midY = rect.yMin + rect.height * 0.5f;
            var halfH = rect.height * 0.5f - 1f;

            // 上辺 (左→右) → 下辺 (右→左) で閉じた上下対称シルエットを塗る。
            painter.fillColor = WaveColor;
            painter.BeginPath();
            painter.MoveTo(new Vector2(rect.xMin, midY));
            var n = _peaks.Length;
            for (var i = 0; i < n; i++)
            {
                var x = rect.xMin + rect.width * (i / (float)(n - 1));
                var a = Mathf.Max(Mathf.Clamp01(_peaks[i]), MinAmplitude);
                painter.LineTo(new Vector2(x, midY - a * halfH));
            }
            for (var i = n - 1; i >= 0; i--)
            {
                var x = rect.xMin + rect.width * (i / (float)(n - 1));
                var a = Mathf.Max(Mathf.Clamp01(_peaks[i]), MinAmplitude);
                painter.LineTo(new Vector2(x, midY + a * halfH));
            }
            painter.ClosePath();
            painter.Fill();

            // 中央線 (無音基準)。
            painter.strokeColor = CenterColor;
            painter.lineWidth = 1f;
            painter.BeginPath();
            painter.MoveTo(new Vector2(rect.xMin, midY));
            painter.LineTo(new Vector2(rect.xMax, midY));
            painter.Stroke();
        }
    }
}
