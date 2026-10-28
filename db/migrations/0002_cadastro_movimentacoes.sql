-- =====================================================================================
-- KRF Reader - migração 0002: cadastro completo, última detecção, passagens e views de consulta
--
-- Roda uma única vez por banco (PRAGMA user_version < 2), dentro de transação: ALTER TABLE ADD COLUMN
-- não é idempotente por si, quem garante é o SchemaMigrator. Dados existentes são preservados.
--
--   * TagCadastro  + Categoria, Unidade, Localizacao (opcionais).
--   * Tags         + UltimaAntenaId, UltimaRssi: contexto da última detecção (UltimaLeitura).
--                    Backfill a partir do último evento quando ele coincide com UltimaLeitura;
--                    nos demais casos fica NULL até a próxima leitura.
--   * Passagens    = passagens fechadas do módulo de alta densidade (veredito de leitura).
--   * TagsCadastradas (view) = cadastro + status atual + última detecção. Tag cadastrada e nunca
--                    lida aparece com Status/UltimaDeteccao NULL.
--   * Movimentacoes (view)   = Eventos + item do cadastro + passagem + flag de anomalia.
--                    Tipo = ENTRADA/SAIDA (modo em que a tag foi lida).
--
-- Por que views e não tabelas novas: Tags guarda estado de toda tag lida (inclusive sem cadastro) e
-- TagCadastro guarda só o dado mestre. Copiar status para TagCadastro ou eventos para outra tabela
-- criaria duas fontes da verdade. As views dão a leitura "uma linha por item" sem duplicar dado.
-- =====================================================================================

ALTER TABLE TagCadastro ADD COLUMN Categoria   TEXT NULL CHECK (Categoria   IS NULL OR length(Categoria)   <= 64);
ALTER TABLE TagCadastro ADD COLUMN Unidade     TEXT NULL CHECK (Unidade     IS NULL OR length(Unidade)     <= 16);
ALTER TABLE TagCadastro ADD COLUMN Localizacao TEXT NULL CHECK (Localizacao IS NULL OR length(Localizacao) <= 100);

ALTER TABLE Tags ADD COLUMN UltimaAntenaId INTEGER NULL;
ALTER TABLE Tags ADD COLUMN UltimaRssi     REAL    NULL;

UPDATE Tags
SET UltimaAntenaId = (SELECT e.AntenaId FROM Eventos e
                      WHERE e.EpcId = Tags.EpcId AND e.Timestamp = Tags.UltimaLeitura
                      ORDER BY e.Id DESC LIMIT 1),
    UltimaRssi     = (SELECT e.Rssi FROM Eventos e
                      WHERE e.EpcId = Tags.EpcId AND e.Timestamp = Tags.UltimaLeitura
                      ORDER BY e.Id DESC LIMIT 1);

CREATE TABLE IF NOT EXISTS Passagens (
    Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    Modo               TEXT    NOT NULL CHECK (Modo IN ('ENTRADA', 'SAIDA')),
    InicioUtc          TEXT    NOT NULL,
    FimUtc             TEXT    NOT NULL,
    Tags               INTEGER NOT NULL,
    Leituras           INTEGER NOT NULL,
    Rodadas            INTEGER NOT NULL,
    EstimativaPerdidas REAL    NOT NULL,
    Veredito           TEXT    NOT NULL CHECK (Veredito IN ('CONFIAVEL', 'ATENCAO', 'NAO_CONVERGIU', 'DADOS_INCOMPLETOS')),
    MotivoFechamento   TEXT    NULL,
    Problemas          TEXT    NULL,
    CHECK (FimUtc >= InicioUtc)
);

-- Vínculo movimentação → passagem (por modo e janela de tempo).
CREATE INDEX IF NOT EXISTS IX_Passagens_Modo_Inicio ON Passagens (Modo, InicioUtc);

-- Filtro por tipo (ENTRADA/SAIDA) + período na consulta de movimentações.
CREATE INDEX IF NOT EXISTS IX_Eventos_Modo_Timestamp ON Eventos (Modo, Timestamp);

CREATE VIEW IF NOT EXISTS TagsCadastradas AS
SELECT
    c.EpcId,
    c.Descricao,
    c.Sku,
    c.Categoria,
    c.Unidade,
    c.Localizacao,
    c.Observacao,
    c.CadastradoEm,
    c.AtualizadoEm,
    t.Status,
    t.UltimaLeitura           AS UltimaDeteccao,
    t.UltimaAntenaId,
    t.UltimaRssi,
    t.UltimaAtualizacaoStatus
FROM TagCadastro c
LEFT JOIN Tags t ON t.EpcId = c.EpcId;

CREATE VIEW IF NOT EXISTS Movimentacoes AS
SELECT
    e.Id,
    e.EpcId,
    c.Descricao,
    c.Sku,
    e.Modo      AS Tipo,
    e.Tipo      AS Evento,
    e.Timestamp AS DataHora,
    e.AntenaId,
    e.Rssi,
    (SELECT p.Id FROM Passagens p
     WHERE p.Modo = e.Modo AND p.InicioUtc <= e.Timestamp AND p.FimUtc >= e.Timestamp
     ORDER BY p.InicioUtc DESC LIMIT 1) AS PassagemId,
    CASE WHEN e.Tipo = 'SAIDA_SEM_ENTRADA' THEN 1 ELSE 0 END AS Anomalia
FROM Eventos e
LEFT JOIN TagCadastro c ON c.EpcId = e.EpcId;
