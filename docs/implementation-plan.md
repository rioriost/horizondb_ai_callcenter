# 実装プラン

## 前提

- 設計レビューは PASS 済み。
- ローカル環境には .NET SDK 10、Azure Developer CLI、Azure CLI、Docker がある。
- `azd up` の必須経路は Container Apps 配信 SPA + API 側 Azure Speech STT + HorizonDB + Azure OpenAI とする。
- 電話番号取得など規制・契約に依存する作業は将来の ACS ingress として分離し、今回の自動化必須経路には含めない。

## 実装方針

1. `.gitignore` と進捗管理ドキュメントを先に整備する。
2. .NET solution を作成する。
3. API は ASP.NET Core Minimal API とし、同じ Container Apps から SPA を静的配信する。
4. DB 初期化 SQL は `infra/scripts/schema.sql` と `infra/scripts/seed.sql` に分け、deploymentScript から再実行可能にする。
5. Azure リソースは Bicep module に分割し、`azure.yaml` から `azd up` で provision/deploy できる形にする。
6. まとまった単位で git commit する。

## 成果物

| パス | 内容 |
| --- | --- |
| `.gitignore` | Azure/.NET/Docker/local secret/audio artifact を除外する。 |
| `azure.yaml` | azd project 定義。API を Container Apps にデプロイする。 |
| `infra/main.bicep` | 全 Azure リソースの入口。 |
| `infra/modules/*.bicep` | HorizonDB、Container Apps、AI Services、Key Vault、ACR 等の module。 |
| `infra/scripts/schema.sql` | HorizonDB schema/extensions/functions。 |
| `infra/scripts/seed.sql` | 初期想定応答マスター。 |
| `src/AiCallCenter.Api` | SPA、会話 API、音声 WebSocket、Azure Speech STT bridge、HorizonDB repository、応答選択 service。 |
| `docs/progress.md` | 実装進捗と検証結果。 |

## マイルストーン

### M1: リポジトリ基盤

- `.gitignore` を追加する。
- `docs/progress.md` を作成する。
- 初回 commit: 要件・設計・レビュー・実装計画・gitignore。

### M2: .NET アプリ基盤

- solution と API project を作成する。
- API に `/healthz`、会話作成、transcript 受信、応答要求の endpoint を追加する。
- ストリーム用に `/ws/conversations/{id}` と `/ws/audio/conversations/{id}` を追加し、PoC では HTTP transcript chunk、WebSocket transcript chunk、ブラウザ PCM 音声のすべてを同じ service に流す。
- Dockerfile を追加し、ローカル build を通す。
- commit: アプリ基盤。

### M3: HorizonDB schema とアプリの DB ロジック

- `conversation_segments`, `response_master`, `response_events` の DDL を作成する。
- API に Npgsql repository を実装する。
- transcript chunk UPSERT と発話確定時の応答選択 SQL を実装する。
- `azure_openai.create_embeddings('app-embedding', ...)` と `azure_ai.rank(..., 'app-reranker')` を使う SQL を実装する。
- commit: HorizonDB 連携。

### M4: ブラウザ音声入力 SPA

- SPA で `getUserMedia()` によりマイク音声を取得し、16 kHz PCM chunk として API WebSocket に送る。
- API は managed identity で Azure Speech STT に接続し、認識結果を既存 transcript flow に送る。
- API から返る応答テキストを SPA に表示する。
- 設定不足は明示エラーにする。
- commit: ブラウザ音声入力。

### M5: Azure インフラ

- Bicep 作成前に HorizonDB preview API version、parameter group の shape、`model_registry.model_add(...)` の引数、reranker model 名を Azure MCP Server tools または Microsoft Learn の一次情報で再確認する。
- `azure.yaml` と Bicep を作成する。
- HorizonDB parameter group で `vector,azure_ai` を許可する。
- deploymentScript で firewall rule、DB 作成、extension 作成、BYOM モデル登録、schema/seed 適用を行う。
- Azure OpenAI は `text-embedding-3-large` と reranker 対応モデルを deployment として作成し、Speech は Azure AI Speech resource として作成する。
- API の managed identity に Azure OpenAI User と Azure AI Speech User を付与する。
- Key Vault と Container Apps secret/reference を設定する。
- commit: azd インフラ。

### M6: 検証と修正ループ

- `dotnet build` を通す。
- Bicep build/what-if 可能な範囲で検証する。
- deploymentScript のログまたは DB 照会で `app-embedding` と `app-reranker` の登録成功を確認する。
- `azd package` / `azd provision` / `azd up` を試行し、失敗箇所を修正する。
- Azure 認証・quota・preview access 等の外部要因で `azd up` が完了しない場合は、失敗内容を `docs/progress.md` に残し、コード側で解消可能な問題をすべて修正する。
- 最終 commit: deployment readiness。

## 実装時のレビュー基準

- Secret がリポジトリに入っていない。
- setup script は再実行可能で、失敗時に非 0 終了する。
- `azure_ai` の BYOM 登録が `azd up` 経路に含まれている。
- API は DB/AI/Speech の設定不足を明示エラーにする。
- SPA はブラウザに credential を保持せず、Azure Speech への認証は API 側 managed identity に集約する。

## 進捗管理

- 作業開始・完了・検証結果を `docs/progress.md` に記録する。
- 内部の作業状態は todo DB でも管理する。
- commit は M1/M2/M3/M4/M5/M6 の境界を基本に行う。
