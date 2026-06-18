# 設計レビュー

## 結果

PASS

## レビュー観点

- `draft.md` の要求が設計上の責務に割り当てられていること。
- HorizonDB の `vector` / `azure_ai` 拡張、UPSERT、コサイン距離、rerank の前提が一次情報に沿っていること。
- `azd up` で secret 生成、HorizonDB 拡張許可、DB 初期化、モデル登録、サービスデプロイまで自動化する方針になっていること。
- USB マイク PoC と将来の電話入口が分離され、同じ API 契約に収束していること。
- プレビュー機能やモデル登録の失敗を silent fallback にせず、明示的に失敗させること。

## 指摘と対応

| 指摘 | 対応 |
| --- | --- |
| `azure_ai` 関数は拡張作成だけでは使えず、AIMM または `model_registry.model_add(...)` が必要。 | 設計に BYOM モデル登録を追加し、`azd up` の deploymentScript で `app-embedding` と `app-reranker` を登録する方針に修正した。 |
| `text-embedding-3-large` は AIMM の default embedding ではないため BYOM が必要。 | `vector(3072)` と `app-embedding` を明示し、`azure_openai.create_embeddings('app-embedding', ...)` を使う方針に統一した。 |
| reranker が利用できない場合の fallback が曖昧。 | reranker deployment がない場合は構成エラーとして停止し、LLM プロンプト rerank は明示フラグがある場合のみ使う方針に修正した。 |

## 判定

ブロッカーは解消済み。実装プラン作成へ進む。
