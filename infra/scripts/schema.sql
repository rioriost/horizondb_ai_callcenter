CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS azure_ai;

CREATE TABLE IF NOT EXISTS conversation_segments (
    conversation_id uuid NOT NULL,
    sequence_no integer NOT NULL,
    partial_text text NOT NULL DEFAULT '',
    final_text text,
    embedding vector(3072),
    status text NOT NULL DEFAULT 'streaming',
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pk_conversation_segments PRIMARY KEY (conversation_id, sequence_no),
    CONSTRAINT chk_conversation_segments_status CHECK (status IN ('streaming', 'finalized', 'responded')),
    CONSTRAINT chk_conversation_segments_sequence CHECK (sequence_no >= 0)
);

CREATE TABLE IF NOT EXISTS response_master (
    id uuid PRIMARY KEY,
    response_text text NOT NULL,
    embedding vector(3072),
    category text NOT NULL DEFAULT 'general',
    enabled boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS response_events (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    conversation_id uuid NOT NULL,
    sequence_no integer NOT NULL,
    selected_response_id uuid NOT NULL REFERENCES response_master(id),
    distance double precision NOT NULL,
    rerank_score double precision NOT NULL,
    spoken_text text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT fk_response_events_segment
        FOREIGN KEY (conversation_id, sequence_no)
        REFERENCES conversation_segments(conversation_id, sequence_no)
);

CREATE INDEX IF NOT EXISTS ix_response_master_enabled ON response_master (enabled) WHERE enabled = true;
CREATE INDEX IF NOT EXISTS ix_response_events_conversation ON response_events (conversation_id, sequence_no, created_at DESC);

