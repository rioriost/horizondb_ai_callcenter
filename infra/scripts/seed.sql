WITH seed_responses(id, response_text, category) AS (
    VALUES
        ('00000000-0000-0000-0000-000000000001'::uuid, 'お問い合わせありがとうございます。ご本人確認のため、お名前とご登録のお電話番号を教えてください。', 'identity'),
        ('00000000-0000-0000-0000-000000000002'::uuid, 'ご不便をおかけして申し訳ありません。状況を確認しますので、発生している問題をもう少し詳しく教えてください。', 'troubleshooting'),
        ('00000000-0000-0000-0000-000000000003'::uuid, '料金や請求内容について確認します。対象の請求月または請求番号が分かれば教えてください。', 'billing'),
        ('00000000-0000-0000-0000-000000000004'::uuid, '契約内容の変更をご希望ですね。現在の契約内容を確認したうえで、変更可能な選択肢をご案内します。', 'contract'),
        ('00000000-0000-0000-0000-000000000005'::uuid, '解約についてのご相談ですね。手続き前に注意事項と代替プランをご案内します。', 'cancellation'),
        ('00000000-0000-0000-0000-000000000006'::uuid, '担当者への引き継ぎが必要な内容です。会話内容を記録したうえで、オペレーターにおつなぎします。', 'handoff')
)
INSERT INTO response_master (id, response_text, category, embedding, enabled, updated_at)
SELECT
    id,
    response_text,
    category,
    NULL,
    true,
    now()
FROM seed_responses
ON CONFLICT (id)
DO UPDATE SET
    response_text = EXCLUDED.response_text,
    category = EXCLUDED.category,
    enabled = true,
    updated_at = now();
