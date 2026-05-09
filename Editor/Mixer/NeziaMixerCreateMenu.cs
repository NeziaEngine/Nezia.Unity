using Unity.GraphToolkit.Editor;
using UnityEditor;

namespace Nezia.Unity.Editor.Mixer
{
    /// <summary>
    /// <c>Assets &gt; Create &gt; Nezia &gt; Mixer Graph</c> メニュー。
    /// 選択中のフォルダに新しい <see cref="NeziaMixerGraph"/> を作成し、
    /// Project ビューでリネーム入力状態にする (GTK 標準の
    /// <see cref="GraphDatabase.PromptInProjectBrowserToCreateNewAsset{T}"/>)。
    /// </summary>
    internal static class NeziaMixerCreateMenu
    {
        [MenuItem("Assets/Create/Nezia/Mixer Graph", priority = 200)]
        private static void Create()
        {
            GraphDatabase.PromptInProjectBrowserToCreateNewAsset<NeziaMixerGraph>();
        }
    }
}
