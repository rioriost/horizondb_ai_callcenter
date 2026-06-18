# AI Callcenter PoC 設計

## 目的

Azure HorizonDB を会話状態とベクター検索の中核に置き、顧客音声から応答音声までをリアルタイムに近い形で処理する AI コールセンター PoC を `azd up` で起動できる構成にする。

## 一次情報からの設計前提

- Azure HorizonDB は PostgreSQL 互換のマネージド DBaaS で、ネイティブなベクター埋め込み、Azure AI Foundry Tools 統合、読み取りレプリカ、ゾーン回復性ストレージを提供する。
- HorizonDB はプレビューであり、利用可能リージョンは限定される。初期実装では `centralus`, `westus2`, `westus3`, `swedencentral`, `australiaeast` を候補にする。
- `pgvector` は拡張名 `vector` として許可・作成する必要があり、コサイン距離は `<=>` 演算子を使う。
- `azure_ai` 拡張は `azure_ai.rank()`、`azure_openai.create_embeddings()` を提供する。利用前に `azure.extensions` で `vector,azure_ai` を許可し、対象 DB で `CREATE EXTENSION` し、さらにモデルを `model_registry.model_add(...)` で登録する必要がある。
- 要件上 `text-embedding-3-large` を使うため、AIMM の `default-embedding` (`text-embedding-3-small`) には依存せず、BYOM として `app-embedding` を登録する。reranker も `app-reranker` として登録し、`azure_ai.rank(..., 'app-reranker')` を明示的に呼ぶ。
- HorizonDB の管理 API/Bicep は `Microsoft.HorizonDb/*@2026-01-20-preview` を使う。
- `azd up` はリポジトリルートの `azure.yaml` と `infra/main.bicep` を入口に、プロビジョニングとサービスデプロイを実行する。

## アーキテクチャ

```text
USB microphone / future phone call
        |
        v
Local microphone adapter or ACS call ingress
        |
        v
Azure AI Speech STT -> transcript stream
        |
        v
Container Apps API
  - session state
  - transcript UPSERT
  - utterance boundary detection
  - response selection
        |
        v
Azure HorizonDB
  - conversation_segments
  - response_master
  - vector search with pgvector
  - rerank with azure_ai.rank
        |
        v
Container Apps API -> Azure AI Speech TTS -> caller / local speaker
```

### Azure リソース

| リソース | 用途 |
| --- | --- |
| Azure Container Apps | 会話オーケストレーター API をホストする。WebSocket/HTTP でストリームを受ける。 |
| Azure Container Registry | `azd deploy` がビルドした API コンテナーを保持する。 |
| Azure HorizonDB | 会話ストリーム、埋め込み、想定応答マスター、検索結果を保持する。 |
| HorizonDB parameter group | `azure.extensions=vector,azure_ai` を許可する。 |
| Azure OpenAI / Foundry model deployments | `text-embedding-3-large`、チャット/整形用モデル、必要に応じた reranker を提供する。 |
| Azure AI Speech | リアルタイム STT と TTS を提供する。 |
| Azure Key Vault | DB パスワード、Speech キー、OpenAI キーなどを保存する。 |
| User-assigned managed identity | Container Apps と deploymentScript が Azure リソースへアクセスする。 |
| Log Analytics | Container Apps のログを保持する。 |

Azure Communication Services は電話番号・通話制御の本番入口として設計に含めるが、電話番号取得や通信規制に依存する作業は PoC の `azd up` 必須経路から外す。開発・デバッグ時は C# のローカル USB マイクアダプターで同じ API に接続する。

## アプリケーション構成

```text
src/AiCallCenter.Api
  ASP.NET Core Minimal API
  /healthz
  /api/conversations
  /api/conversations/{id}/transcript
  /api/conversations/{id}/respond
  /ws/conversations/{id}

src/AiCallCenter.LocalAdapter
  C# console app
  USB microphone -> Azure AI Speech STT
  transcript chunks -> API
  response text -> Azure AI Speech TTS playback
```

API は音声処理の入口を抽象化する。ローカルアダプターと将来の ACS ingress は、どちらも「transcript chunk」と「utterance finalized」を API に送る。API 側は音声デバイスや電話回線に依存せず、HorizonDB と応答選択に集中する。

## データモデル

### `conversation_segments`

| 列 | 型 | 説明 |
| --- | --- | --- |
| `conversation_id` | `uuid` | 会話 ID。 |
| `sequence_no` | `integer` | 顧客発話単位の連番。 |
| `partial_text` | `text` | ストリーミング中の暫定テキスト。UPSERT で更新する。 |
| `final_text` | `text` | 発話確定時のテキスト。 |
| `embedding` | `vector(3072)` | `text-embedding-3-large` の埋め込み。 |
| `status` | `text` | `streaming`, `finalized`, `responded`。 |
| `created_at` / `updated_at` | `timestamptz` | 監査用。 |

主キーは `(conversation_id, sequence_no)`。ストリーム中は同じキーに `INSERT ... ON CONFLICT DO UPDATE` し、発話が途切れたタイミングで `final_text` と `embedding` を確定する。

### `response_master`

| 列 | 型 | 説明 |
| --- | --- | --- |
| `id` | `uuid` | 想定応答 ID。 |
| `response_text` | `text` | 顧客へ返す基準応答。 |
| `embedding` | `vector(3072)` | 応答テキストの埋め込み。 |
| `category` | `text` | FAQ 分類。 |
| `enabled` | `boolean` | 検索対象フラグ。 |
| `created_at` / `updated_at` | `timestamptz` | 監査用。 |

初期データは migration で投入し、埋め込みは HorizonDB の `azure_openai.create_embeddings('app-embedding', ...)` で生成する。`vector(3072)` は PoC の小規模マスターでは exact scan を許容する。大規模化時は DiskANN または `halfvec(3072)` への切り替えを検討する。

### `response_events`

| 列 | 型 | 説明 |
| --- | --- | --- |
| `id` | `uuid` | 応答イベント ID。 |
| `conversation_id` | `uuid` | 会話 ID。 |
| `sequence_no` | `integer` | 対象発話。 |
| `selected_response_id` | `uuid` | 採用した想定応答。 |
| `distance` | `double precision` | ベクター検索距離。 |
| `rerank_score` | `double precision` | reranker スコア。 |
| `spoken_text` | `text` | 実際に読み上げるテキスト。 |

## 応答選択フロー

1. API が transcript chunk を受信する。
2. `conversation_segments` に `(conversation_id, sequence_no)` で UPSERT する。
3. 発話確定時に `final_text` を保存し、`azure_openai.create_embeddings('app-embedding', final_text)` で `embedding` を生成する。
4. `response_master` を `ORDER BY embedding <=> query_embedding LIMIT 20` で候補取得する。
5. 候補が複数ある場合は `azure_ai.rank(final_text, response_text[], id[], 'app-reranker')` で再ランクし、返却される `document_id`, `rank`, `relevance_score` を `response_events` の `selected_response_id`, `rerank_score` に対応付ける。
6. 最上位応答を `response_events` に記録する。
7. API が response text を返し、ローカルアダプターまたは通話入口が Azure AI Speech TTS で音声化する。

## `azd up` の自動化範囲

`azd up` では次を自動化する。

- リージョンとリソースグループは azd の標準入力に任せる。
- ランダムな HorizonDB 管理者パスワードを Bicep で生成し、Key Vault に格納する。
- HorizonDB parameter group を作成し、`vector,azure_ai` を許可する。
- HorizonDB cluster 作成後に deploymentScript で firewall rule、DB 作成、拡張作成、BYOM モデル登録、スキーマ作成、初期応答データ投入を行う。
- deploymentScript は Foundry/OpenAI endpoint、embedding deployment、reranker deployment、API key を secure env var として受け取り、次の alias を冪等に登録する。
  - `app-embedding`: `text-embedding-3-large`
  - `app-reranker`: `Cohere-rerank-v4.0-fast` または Foundry で利用可能な GPT rerank 対応モデル
- Azure OpenAI/Speech/Container Apps/ACR/Key Vault/Log Analytics を作成する。
- API コンテナーをビルドして Container Apps にデプロイする。
- API に必要な値は Key Vault reference または Container Apps secret として注入する。

## セキュリティ

- Secret はソースコードや `azure.yaml` に保存しない。
- HorizonDB 管理者パスワード、Speech key、OpenAI key は Key Vault に保存する。
- Container Apps は User-assigned managed identity を使い、Key Vault 参照で secret を取得する。
- HorizonDB firewall は PoC では Azure services と deployer IP を許可する。本番では private endpoint と最小許可に切り替える。
- API の管理・検証用エンドポイントは `/healthz` のみ匿名公開し、会話 API は将来認証を追加できるように境界を分ける。

## 非機能要件

- レイテンシ: 発話確定後の応答候補選択は PoC で 1 秒以内を目標にする。
- 可観測性: transcript 受信、UPSERT、embedding、vector search、rerank、TTS 要求単位で構造化ログを出す。
- 冪等性: schema/setup script は再実行可能にする。
- 拡張性: ACS ingress を後から追加しても API の HorizonDB/応答選択ロジックは変更しない。

## リスクと対応

| リスク | 対応 |
| --- | --- |
| HorizonDB がプレビューで Bicep/API が変わる | `2026-01-20-preview` に固定し、deploymentScript は失敗時に明示エラーで止める。 |
| `azure_ai` モデル登録の権限・リージョン制約 | AIMM には依存せず BYOM 登録を `azd up` で実行する。reranker deployment が利用できない場合は構成エラーとして停止し、LLM プロンプトによる簡易 rerank は明示フラグがある場合のみ使う。 |
| 電話番号取得は完全自動化できない可能性 | `azd up` 必須経路は USB マイク PoC とし、ACS は拡張入口として分離する。 |
| 音声ストリーム処理が環境差の影響を受ける | C# Speech SDK アダプターでローカル検証可能にする。 |

## 設計レビュー基準

- 要件の各項目が設計上の責務に割り当てられている。
- HorizonDB の拡張機能、ベクター型、rerank、UPSERT が一次情報に沿っている。
- `azd up` で secret 生成・DB 初期化・サービスデプロイまで到達できる。
- 電話入口と USB マイク PoC の責務が分離されている。
- プレビュー機能の失敗時に沈黙せず、明示的に失敗する。
