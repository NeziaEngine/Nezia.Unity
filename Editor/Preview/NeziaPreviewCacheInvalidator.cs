using System;
using System.Collections.Generic;
using UnityEditor;

namespace Nezia.Unity.Editor.Preview
{
    /// <summary>
    /// IP-6: 音声ファイル / SoundAsset の再インポート・削除・移動を検知して
    /// preview キャッシュ (<see cref="NeziaPreviewSession"/>) を無効化する。
    /// これが無いと、ファイル差し替え後も daemon には旧デコード結果が残り、
    /// 試聴だけ古い音が鳴り続ける (Domain Reload で偶然直るため気づきにくい)。
    /// </summary>
    internal sealed class NeziaPreviewCacheInvalidator : AssetPostprocessor
    {
        /// <summary>
        /// preview キャッシュのキーになり得る拡張子。音声 4 種は BufferCache、
        /// .asset は NeziaRandomContainer 等の ContainerCache キー。
        /// </summary>
        private static readonly string[] RelevantExtensions =
        {
            ".wav", ".ogg", ".flac", ".mp3", ".asset",
        };

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            List<string> changed = null;
            Collect(importedAssets, ref changed);
            Collect(deletedAssets, ref changed);
            // 移動はキャッシュキー (旧パス) 側を無効化する。新パスは初回再生時に
            // 普通にロードされるので何もしなくてよい。
            Collect(movedFromAssetPaths, ref changed);

            if (changed != null)
            {
                NeziaPreviewSession.InvalidateAssets(changed);
            }
        }

        private static void Collect(string[] paths, ref List<string> into)
        {
            foreach (var path in paths)
            {
                foreach (var ext in RelevantExtensions)
                {
                    if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    {
                        (into ??= new List<string>()).Add(path);
                        break;
                    }
                }
            }
        }
    }
}
