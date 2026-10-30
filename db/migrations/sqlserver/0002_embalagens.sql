-- =====================================================================================
-- KRF Reader - SQL Server, migração 0002: embalagens com várias etiquetas (equivale à 0003 do SQLite)
--
-- Ver db/migrations/0003_embalagens.sql para o modelo e a regra de movimentação. Diferenças:
--   * Codigo único pela collation do banco (padrão CI_AS: ignora caixa); a aplicação grava em maiúsculas.
--   * PesoKg DECIMAL(10,3). Datas DATETIME2(3) UTC; views com colunas *Local (Brasília) para o BI.
--   * "GO" separa os lotes: coluna nova só pode ser referenciada no lote seguinte ao ALTER, e CREATE VIEW precisa
--     ser o primeiro comando do lote. Tudo roda na mesma transação do migrador (falha = rollback completo).
-- =====================================================================================

CREATE TABLE dbo.Embalagens (
    Id                      BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Embalagens PRIMARY KEY,
    Codigo                  NVARCHAR(64)  NOT NULL CONSTRAINT UQ_Embalagens_Codigo UNIQUE,
    Descricao               NVARCHAR(200) NULL,
    TipoGlt                 NVARCHAR(32)  NULL,
    ComprimentoMm           INT           NULL CONSTRAINT CK_Embalagens_Comprimento CHECK (ComprimentoMm > 0),
    LarguraMm               INT           NULL CONSTRAINT CK_Embalagens_Largura CHECK (LarguraMm > 0),
    AlturaMm                INT           NULL CONSTRAINT CK_Embalagens_Altura CHECK (AlturaMm > 0),
    PesoKg                  DECIMAL(10,3) NULL CONSTRAINT CK_Embalagens_Peso CHECK (PesoKg > 0),
    Observacao              NVARCHAR(500) NULL,
    CadastradoEm            DATETIME2(3)  NOT NULL,
    AtualizadoEm            DATETIME2(3)  NOT NULL,
    Status                  NVARCHAR(8)   NULL CONSTRAINT CK_Embalagens_Status CHECK (Status IN (N'DENTRO', N'FORA')),
    UltimaDeteccao          DATETIME2(3)  NULL,
    UltimaAtualizacaoStatus DATETIME2(3)  NULL,
    UltimaEpcId             NVARCHAR(128) NULL,
    UltimaAntenaId          INT           NULL,
    UltimaRssi              FLOAT         NULL,
    CONSTRAINT CK_Embalagens_Codigo CHECK (LEN(Codigo) >= 1)
);

CREATE TABLE dbo.EmbalagemEtiquetas (
    EpcId       NVARCHAR(128) NOT NULL CONSTRAINT PK_EmbalagemEtiquetas PRIMARY KEY,
    EmbalagemId BIGINT        NOT NULL
        CONSTRAINT FK_EmbalagemEtiquetas_Embalagens REFERENCES dbo.Embalagens (Id) ON DELETE CASCADE,
    Posicao     NVARCHAR(32)  NULL,
    VinculadoEm DATETIME2(3)  NOT NULL
);

CREATE INDEX IX_EmbalagemEtiquetas_EmbalagemId ON dbo.EmbalagemEtiquetas (EmbalagemId);
CREATE INDEX IX_Embalagens_Status ON dbo.Embalagens (Status);
CREATE INDEX IX_Embalagens_TipoGlt ON dbo.Embalagens (TipoGlt);

ALTER TABLE dbo.Eventos ADD EmbalagemId BIGINT NULL;
ALTER TABLE dbo.Passagens ADD Itens INT NULL, Embalagens INT NULL;
GO

CREATE INDEX IX_Eventos_EmbalagemId_Timestamp ON dbo.Eventos (EmbalagemId, [Timestamp]);

DROP VIEW dbo.TagsCadastradas;
DROP VIEW dbo.Movimentacoes;
GO

-- Tags avulsas + etiquetas de embalagem. Para etiqueta: Status = da embalagem, UltimaDeteccao = desta etiqueta.
CREATE VIEW dbo.TagsCadastradas AS
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
    CONVERT(DATETIME2(3), t.UltimaLeitura AT TIME ZONE 'UTC' AT TIME ZONE 'E. South America Standard Time') AS UltimaDeteccaoLocal,
    CAST(NULL AS BIGINT)        AS EmbalagemId,
    CAST(NULL AS NVARCHAR(64))  AS EmbalagemCodigo,
    CAST(NULL AS NVARCHAR(32))  AS Posicao
FROM dbo.TagCadastro c
LEFT JOIN dbo.Tags t ON t.EpcId = c.EpcId
WHERE NOT EXISTS (SELECT 1 FROM dbo.EmbalagemEtiquetas ee WHERE ee.EpcId = c.EpcId)
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
    CONVERT(DATETIME2(3), t.UltimaLeitura AT TIME ZONE 'UTC' AT TIME ZONE 'E. South America Standard Time'),
    em.Id,
    em.Codigo,
    ee.Posicao
FROM dbo.EmbalagemEtiquetas ee
JOIN dbo.Embalagens em ON em.Id = ee.EmbalagemId
LEFT JOIN dbo.Tags t ON t.EpcId = ee.EpcId;
GO

-- Eventos + item (embalagem ou cadastro de tag) + passagem + flag de anomalia.
CREATE VIEW dbo.Movimentacoes AS
SELECT
    e.Id,
    e.EpcId,
    CASE WHEN e.EmbalagemId IS NOT NULL THEN COALESCE(em.Descricao, em.Codigo) ELSE c.Descricao END AS Descricao,
    CASE WHEN e.EmbalagemId IS NOT NULL THEN NULL ELSE c.Sku END AS Sku,
    e.Modo        AS Tipo,
    e.Tipo        AS Evento,
    e.[Timestamp] AS DataHora,
    e.AntenaId,
    e.Rssi,
    p.PassagemId,
    CAST(CASE WHEN e.Tipo = N'SAIDA_SEM_ENTRADA' THEN 1 ELSE 0 END AS BIT) AS Anomalia,
    CONVERT(DATETIME2(3), e.[Timestamp] AT TIME ZONE 'UTC' AT TIME ZONE 'E. South America Standard Time') AS DataHoraLocal,
    e.EmbalagemId,
    em.Codigo     AS EmbalagemCodigo,
    em.TipoGlt
FROM dbo.Eventos e
LEFT JOIN dbo.TagCadastro c ON c.EpcId = e.EpcId
LEFT JOIN dbo.Embalagens em ON em.Id = e.EmbalagemId
OUTER APPLY (
    SELECT TOP (1) pa.Id AS PassagemId
    FROM dbo.Passagens pa
    WHERE pa.Modo = e.Modo AND pa.InicioUtc <= e.[Timestamp] AND pa.FimUtc >= e.[Timestamp]
    ORDER BY pa.InicioUtc DESC
) p;
GO

CREATE VIEW dbo.EmbalagensCadastradas AS
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
    (SELECT COUNT(*) FROM dbo.EmbalagemEtiquetas ee WHERE ee.EmbalagemId = em.Id) AS Etiquetas,
    CONVERT(DATETIME2(3), em.UltimaDeteccao AT TIME ZONE 'UTC' AT TIME ZONE 'E. South America Standard Time') AS UltimaDeteccaoLocal
FROM dbo.Embalagens em;
GO

-- Um item de estoque por linha: embalagem com estado + tag lida que não é etiqueta de embalagem.
CREATE VIEW dbo.ItensEstoque AS
SELECT
    N'EMBALAGEM'                      AS TipoItem,
    em.Codigo                         AS Item,
    COALESCE(em.Descricao, em.Codigo) AS Descricao,
    em.Status,
    em.UltimaDeteccao,
    em.UltimaAtualizacaoStatus,
    em.Id                             AS EmbalagemId
FROM dbo.Embalagens em
WHERE em.Status IS NOT NULL
UNION ALL
SELECT
    N'TAG',
    t.EpcId,
    c.Descricao,
    t.Status,
    t.UltimaLeitura,
    t.UltimaAtualizacaoStatus,
    NULL
FROM dbo.Tags t
LEFT JOIN dbo.TagCadastro c ON c.EpcId = t.EpcId
WHERE NOT EXISTS (SELECT 1 FROM dbo.EmbalagemEtiquetas ee WHERE ee.EpcId = t.EpcId);
