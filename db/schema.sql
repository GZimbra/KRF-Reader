-- =====================================================================================
-- KRF Reader - schema do banco (SQLite)
--
-- Idempotente: pode ser executado a cada inicialização (CREATE ... IF NOT EXISTS).
-- Este arquivo é a fonte única do schema: é embutido no assembly KrfReader.Infrastructure
-- (recurso "KrfReader.Infrastructure.schema.sql") e executado por
-- SqliteInventoryRepository.InitializeAsync.
--
-- Modelo:
--   * Tags    = estado atual de cada tag (uma linha por EPC).
--   * Eventos = histórico de auditoria. Registra SOMENTE mudança de estado
--               (CRIACAO, MUDANCA_STATUS) e anomalia (SAIDA_SEM_ENTRADA).
--               Releitura idempotente (tag DENTRO lida em ENTRADA, FORA lida em SAIDA)
--               NÃO gera evento: apenas atualiza Tags.UltimaLeitura.
--
-- Datas: TEXT em ISO-8601 UTC com milissegundos, formato fixo 'yyyy-MM-ddTHH:mm:ss.fffZ'
--   (ex.: '2026-10-26T13:45:07.123Z'). Largura fixa => ordenação lexicográfica = cronológica,
--   então ORDER BY / MAX() / índices funcionam direto sobre o texto.
--
-- PRAGMAs (aplicados pela aplicação, não aqui): journal_mode=WAL (persistente no arquivo),
--   synchronous=NORMAL, foreign_keys=ON e busy_timeout (por conexão).
--
-- Portabilidade:
--   PostgreSQL : upsert idêntico (INSERT ... ON CONFLICT (EpcId) DO UPDATE SET ... excluded.*);
--                datas como TIMESTAMPTZ; Id como BIGSERIAL (ou GENERATED ALWAYS AS IDENTITY);
--                Rssi como DOUBLE PRECISION; CHECKs iguais; LIKE -> ILIKE se quiser case-insensitive.
--   SQL Server : upsert via MERGE (WITH (HOLDLOCK)) ou UPDATE + INSERT; datas como DATETIME2(3)
--                (UTC); Id como BIGINT IDENTITY(1,1); EpcId como VARCHAR(64); Rssi como FLOAT;
--                "IF NOT EXISTS" via checagem em sys.tables / sys.indexes; LIMIT -> TOP / OFFSET-FETCH.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS Tags (
    EpcId                   TEXT NOT NULL PRIMARY KEY,
    Status                  TEXT NOT NULL CHECK (Status IN ('DENTRO', 'FORA')),
    UltimaLeitura           TEXT NOT NULL,
    UltimaAtualizacaoStatus TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS Eventos (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    EpcId     TEXT    NOT NULL,
    Modo      TEXT    NOT NULL CHECK (Modo IN ('ENTRADA', 'SAIDA')),
    Tipo      TEXT    NOT NULL CHECK (Tipo IN ('CRIACAO', 'MUDANCA_STATUS', 'SAIDA_SEM_ENTRADA')),
    Timestamp TEXT    NOT NULL,
    AntenaId  INTEGER NOT NULL,
    Rssi      REAL    NOT NULL,
    FOREIGN KEY (EpcId) REFERENCES Tags (EpcId)
);

-- Contadores DENTRO/FORA e filtro por status na consulta.
CREATE INDEX IF NOT EXISTS IX_Tags_Status ON Tags (Status);

-- Listagem de tags ordenada por última leitura (mais recente primeiro).
CREATE INDEX IF NOT EXISTS IX_Tags_UltimaLeitura ON Tags (UltimaLeitura);

-- Histórico de uma tag (WHERE EpcId = ? ORDER BY Timestamp DESC).
CREATE INDEX IF NOT EXISTS IX_Eventos_EpcId_Timestamp ON Eventos (EpcId, Timestamp);

-- Consultas por período.
CREATE INDEX IF NOT EXISTS IX_Eventos_Timestamp ON Eventos (Timestamp);

-- Contagem de anomalias (Tipo = 'SAIDA_SEM_ENTRADA').
CREATE INDEX IF NOT EXISTS IX_Eventos_Tipo ON Eventos (Tipo);
