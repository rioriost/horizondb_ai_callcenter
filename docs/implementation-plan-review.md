# 実装プランレビュー

## 結果

PASS

## レビュー観点

- `.gitignore` が実装前に設定されること。
- 進捗管理ドキュメントと commit 境界が明確であること。
- HorizonDB の extension 許可、DB 初期化、BYOM モデル登録が `azd up` 経路に含まれていること。
- USB マイク PoC と API のストリーミング経路が実装対象に含まれていること。
- `azd up` までの検証・修正ループが計画されていること。

## 指摘と対応

| 指摘 | 対応 |
| --- | --- |
| Azure MCP Server / 一次情報で preview API と model registry の最新 shape を確認する工程が明示されていない。 | M5 に HorizonDB API version、parameter group、`model_registry.model_add(...)`、reranker model 名の再確認を追加した。 |
| 設計上の WebSocket endpoint がマイルストーンに明示されていない。 | M2 に `/ws/conversations/{id}` の実装を追加した。 |
| reranker alias 登録成功の確認が検証項目にない。 | M6 に `app-embedding` と `app-reranker` 登録成功確認を追加した。 |

## 判定

ブロッカーはなし。`.gitignore` 設定後に実装へ進む。
