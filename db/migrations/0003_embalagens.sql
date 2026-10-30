-- =====================================================================================
-- KRF Reader - migração 0003: embalagens com várias etiquetas (GLT com 2 tags on-metal em faces opostas)
--
-- Roda uma única vez por banco (PRAGMA user_version < 3), dentro de transação. Dados existentes preservados:
-- nada muda para tag avulsa (1 EPC = 1 item).
--
--   * Embalagens         = embalagem física (item de estoque) + estado atual. Codigo é a identificação física
--                          (único, maiúsculas, sem distinção de caixa). TipoGlt (ex.: GLT1165) é o MODELO e se repete,
--                          inclusive com medidas diferentes: não é chave. Estado (Status, UltimaDeteccao...) fica aqui
--                          e não em TagCadastro/Tags: a embalagem só existe via cadastro, então não há duas fontes.
--   * EmbalagemEtiquetas = EPC → embalagem. EpcId é PK: uma etiqueta pertence a no máximo uma embalagem.
--                          A aplicação recusa EPC que também esteja em TagCadastro (tag avulsa).
--   * Eventos.EmbalagemId = embalagem movimentada; o EpcId do evento é a etiqueta que foi lida. Sem FK: apagar o
--                          cadastro da embalagem não apaga nem bloqueia o histórico.
--   * Passagens.Itens/Embalagens = contagem por item (veredito) e embalagens detectadas. NULL em passagens antigas.
--
-- Regra de movimentação (aplicada pela aplicação, não por trigger): leitura de qualquer etiqueta decide sobre o
-- status DA EMBALAGEM. Segunda etiqueta da mesma caixa no mesmo modo = releitura, sem evento: UMA movimentação por
-- embalagem por passagem. As linhas de Tags das etiquetas acompanham o status da embalagem.
--
-- Views recriadas (DROP + CREATE; colunas antigas mantidas na mesma ordem, novas no fim, para não quebrar o BI):
--   * TagsCadastradas: + etiquetas de embalagem (Status = da embalagem; UltimaDeteccao = desta etiqueta),
--                      + EmbalagemId, EmbalagemCodigo, Posicao (NULL para tag avulsa).
--   * Movimentacoes:   + EmbalagemId, EmbalagemCodigo, TipoGlt. Descricao = da embalagem (ou o código) quando houver.
--   * EmbalagensCadastradas (nova): uma linha por embalagem com estado e número de etiquetas.
--   * ItensEstoque (nova): uma linha por item de estoque (embalagem com estado + tag lida fora de embalagem).
--                      Base dos contadores DENTRO/FORA.
-- =====================================================================================

CREATE TABLE Embalagens (
    Id                      INTEGER PRIMARY KEY AUTOINCREMENT,
    Codigo                  TEXT    NOT NULL COLLATE NOCASE UNIQUE CHECK (length(Codigo) BETWEEN 1 AND 64),
    Descricao               TEXT    NULL CHECK (Descricao  IS NULL OR length(Descricao)  <= 200),
    TipoGlt                 TEXT    NULL CHECK (TipoGlt    IS NULL OR length(TipoGlt)    <= 32),
    ComprimentoMm           INTEGER NULL CHECK (ComprimentoMm IS NULL OR ComprimentoMm > 0),
    LarguraMm               INTEGER NULL CHECK (LarguraMm     IS NULL OR LarguraMm     > 0),
    AlturaMm                INTEGER NULL CHECK (AlturaMm      IS NULL OR AlturaMm      > 0),
    PesoKg                  REAL    NULL CHECK (PesoKg        IS NULL OR PesoKg        > 0),
    Observacao              TEXT    NULL CHECK (Observacao IS NULL OR length(Observacao) <= 500),
    CadastradoEm            TEXT    NOT NULL,
    AtualizadoEm            TEXT    NOT NULL,
    Status                  TEXT    NULL CHECK (Status IS NULL OR Status IN ('DENTRO', 'FORA')),
    UltimaDeteccao          TEXT    NULL,
    UltimaAtualizacaoStatus TEXT    NULL,
    UltimaEpcId             TEXT    NULL,
    UltimaAntenaId          INTEGER NULL,
    UltimaRssi              REAL    NULL
);

CREATE TABLE EmbalagemEtiquetas (
    EpcId       TEXT    NOT NULL PRIMARY KEY,
    EmbalagemId INTEGER NOT NULL REFERENCES Embalagens (Id) ON DELETE CASCADE,
    Posicao     TEXT    NULL CHECK (Posicao IS NULL OR length(Posicao) <= 32),
    VinculadoEm TEXT    NOT NULL
);

CREATE INDEX IX_EmbalagemEtiquetas_EmbalagemId ON EmbalagemEtiquetas (EmbalagemId);
CREATE INDEX IX_Embalagens_Status ON Embalagens (Status);
CREATE INDEX IX_Embalagens_TipoGlt ON Embalagens (TipoGlt);

ALTER TABLE Eventos ADD COLUMN EmbalagemId INTEGER NULL;
CREATE INDEX IX_Eventos_EmbalagemId_Timestamp ON Eventos (EmbalagemId, Timestamp);

ALTER TABLE Passagens ADD COLUMN Itens      INTEGER NULL;
ALTER TABLE Passagens ADD COLUMN Embalagens INTEGER NULL;

DROP VIEW IF EXISTS TagsCadastradas;
DROP VIEW IF EXISTS Movimentacoes;

CREATE VIEW TagsCadastradas AS
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
    t.UltimaAtualizacaoStatus,
    NULL                      AS EmbalagemId,
    NULL                      AS EmbalagemCodigo,
    NULL                      AS Posicao
FROM TagCadastro c
LEFT JOIN Tags t ON t.EpcId = c.EpcId
WHERE NOT EXISTS (SELECT 1 FROM EmbalagemEtiquetas ee WHERE ee.EpcId = c.EpcId)
UNION ALL
SELECT
    ee.EpcId,
    COALESCE(em.Descricao, em.Codigo),
    NULL,
    NULL,
    NULL,
    NULL,
    em.Observacao,
    ee.VinculadoEm,
    em.AtualizadoEm,
    em.Status,
    t.UltimaLeitura,
    t.UltimaAntenaId,
    t.UltimaRssi,
    em.UltimaAtualizacaoStatus,
    em.Id,
    em.Codigo,
    ee.Posicao
FROM EmbalagemEtiquetas ee
JOIN Embalagens em ON em.Id = ee.EmbalagemId
LEFT JOIN Tags t ON t.EpcId = ee.EpcId;

CREATE VIEW Movimentacoes AS
SELECT
    e.Id,
    e.EpcId,
    CASE WHEN e.EmbalagemId IS NOT NULL THEN COALESCE(em.Descricao, em.Codigo) ELSE c.Descricao END AS Descricao,
    CASE WHEN e.EmbalagemId IS NOT NULL THEN NULL ELSE c.Sku END AS Sku,
    e.Modo      AS Tipo,
    e.Tipo      AS Evento,
    e.Timestamp AS DataHora,
    e.AntenaId,
    e.Rssi,
    (SELECT p.Id FROM Passagens p
     WHERE p.Modo = e.Modo AND p.InicioUtc <= e.Timestamp AND p.FimUtc >= e.Timestamp
     ORDER BY p.InicioUtc DESC LIMIT 1) AS PassagemId,
    CASE WHEN e.Tipo = 'SAIDA_SEM_ENTRADA' THEN 1 ELSE 0 END AS Anomalia,
    e.EmbalagemId,
    em.Codigo   AS EmbalagemCodigo,
    em.TipoGlt
FROM Eventos e
LEFT JOIN TagCadastro c ON c.EpcId = e.EpcId
LEFT JOIN Embalagens em ON em.Id = e.EmbalagemId;

CREATE VIEW EmbalagensCadastradas AS
SELECT
    em.Id AS EmbalagemId,
    em.Codigo,
    em.Descricao,
    em.TipoGlt,
    em.ComprimentoMm,
    em.LarguraMm,
    em.AlturaMm,
    em.PesoKg,
    em.Observacao,
    em.CadastradoEm,
    em.AtualizadoEm,
    em.Status,
    em.UltimaDeteccao,
    em.UltimaEpcId,
    em.UltimaAntenaId,
    em.UltimaRssi,
    em.UltimaAtualizacaoStatus,
    (SELECT COUNT(*) FROM EmbalagemEtiquetas ee WHERE ee.EmbalagemId = em.Id) AS Etiquetas
FROM Embalagens em;

CREATE VIEW ItensEstoque AS
SELECT
    'EMBALAGEM'                       AS TipoItem,
    em.Codigo                         AS Item,
    COALESCE(em.Descricao, em.Codigo) AS Descricao,
    em.Status,
    em.UltimaDeteccao,
    em.UltimaAtualizacaoStatus,
    em.Id                             AS EmbalagemId
FROM Embalagens em
WHERE em.Status IS NOT NULL
UNION ALL
SELECT
    'TAG',
    t.EpcId,
    c.Descricao,
    t.Status,
    t.UltimaLeitura,
    t.UltimaAtualizacaoStatus,
    NULL
FROM Tags t
LEFT JOIN TagCadastro c ON c.EpcId = t.EpcId
WHERE NOT EXISTS (SELECT 1 FROM EmbalagemEtiquetas ee WHERE ee.EpcId = t.EpcId);
