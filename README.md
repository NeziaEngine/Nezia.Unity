# Nezia Sound Engine (jp.nezia.unity)

Nezia Sound Engine。

## インストール

`Packages/manifest.json` に以下を追加:

```json
{
  "dependencies": {
    "jp.nezia.unity": "file:../Packages/jp.nezia.unity"
  }
}
```

または Unity Package Manager の "Add package from disk..." から `package.json` を指定。

## 構成

- `Runtime/` — ランタイムコード (`Nezia.Unity` 名前空間)
- `Editor/` — エディタ拡張 (`Nezia.Unity.Editor` 名前空間)
- `Tests/Runtime/`, `Tests/Editor/` — テスト
