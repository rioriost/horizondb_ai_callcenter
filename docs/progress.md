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

- M1 commit を作成した。
- .NET solution、API、LocalAdapter の基盤を作成した。
- API に `/healthz`、会話作成、transcript POST、respond POST、`/ws/conversations/{id}` を追加した。
- LocalAdapter に USB マイク Speech STT、API 送信、Speech TTS、`--text` smoke test mode を追加した。
- API Dockerfile を追加した。
- `dotnet build AiCallCenter.slnx --no-restore` が成功した。
- M2 commit を作成した。
- HorizonDB schema、seed SQL、Azure OpenAI model alias 登録、response master embedding 生成を `infra/scripts/postprovision.sh` に実装した。
- ARM `deploymentScripts` は Storage Account の key-based authentication がテナントポリシーで拒否されたため、azd `postprovision` hook に移行した。
- Azure OpenAI / Speech は local auth disabled のポリシーに合わせ、API と hook から Entra ID token で呼び出す構成にした。
- HorizonDB の `azure_ai` managed identity 呼び出しはテナント不一致で失敗したため、BYOM alias は登録しつつ、embedding / rerank は API と hook から Azure OpenAI REST を呼び、HorizonDB には `vector(3072)` を保存する構成にした。
- HorizonDB preview の immutable property 更新エラーを回避するため、既存クラスター再利用モードと postprovision hook での parameter group attach を追加した。
- 現在の環境では既存 HorizonDB を安全に再利用するため、`infra/main.parameters.json` で `environmentName` と `useExistingHorizonDb` を固定している。
- `azd up --no-prompt` が成功し、Container Apps API endpoint が発行された。
- 公開 API の `/healthz` が 200 を返すことを確認した。
- 公開 API で会話作成から `/api/conversations/{id}/respond` までの smoke test が成功し、HorizonDB vector search と Azure OpenAI rerank による応答候補が返ることを確認した。
- Microsoft 組織ポリシー下で Speech local auth/key を使わない方針に変更した。
- LocalAdapter project を削除し、Container Apps 配信 SPA と `/ws/audio/conversations/{id}` を追加した。
- ブラウザは `getUserMedia()` で取得した音声を 16 kHz PCM chunk として API に送り、API が managed identity で Azure Speech STT に接続する構成に変更した。
- API managed identity に `Cognitive Services Speech User` role を付与する Bicep を追加した。
- Speech SDK の C# `SpeechRecognizer` は Entra ID 認証で custom endpoint + `TokenCredential` を使う必要があるため、raw bearer token 方式から `SpeechConfig.FromEndpoint(..., TokenCredential)` に変更した。
