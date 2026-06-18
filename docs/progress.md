# 実装進捗

## 2026-06-18

- `draft.md` を読み、HorizonDB / azd / `azure_ai` の一次情報を確認した。
- `docs/design.md` を作成した。
- 設計レビューを実施し、`azure_ai` BYOM モデル登録の不足を修正した。
- `docs/design-review.md` を保存し、設計レビュー PASS とした。
- `docs/implementation-plan.md` を作成した。
- 実装プランレビューを実施し、WebSocket、preview API 再確認、reranker 登録確認をプランへ追記した。
- `docs/implementation-plan-review.md` を保存し、プランレビュー PASS とした。
- 実装前に `.gitignore` を作成した。

## 次の作業

- M1 commit を作成した。
- .NET solution、API、LocalAdapter の基盤を作成した。
- API に `/healthz`、会話作成、transcript POST、respond POST、`/ws/conversations/{id}` を追加した。
- LocalAdapter に USB マイク Speech STT、API 送信、Speech TTS、`--text` smoke test mode を追加した。
- API Dockerfile を追加した。
- `dotnet build AiCallCenter.slnx --no-restore` が成功した。

## 次の作業

- HorizonDB schema と DB 初期化 SQL を作成する。
- API の HorizonDB 連携 SQL と deploymentScript を `azd up` 経路に接続する。
