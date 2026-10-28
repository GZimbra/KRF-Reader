-- =====================================================================================
-- KRF Reader - SQL Server, migração 0001: schema equivalente ao SQLite v2 (0001 + 0002)
--
-- Sequência própria (db/migrations/sqlserver/NNNN_nome.sql), versionada na tabela dbo.SchemaVersao.
-- Cada migração roda numa transação com sp_getapplock: falha no meio = rollback. "GO" sozinho na linha
-- separa lotes (CREATE VIEW precisa ser o primeiro comando do lote). Não edite migração publicada.
--
-- Diferenças em relação ao SQLite:
--   * Datas DATETIME2(3) em UTC (a aplicação trunca para milissegundos antes de gravar).
--   * Texto NVARCHAR; comparação segue a collation do banco (padrão do instalador: CI_AS = ignora caixa).
--   * Views ganham colunas *Local (horário de Brasília, 'E. South America Standard Time') para o BI.
--     Filtros do app usam sempre as colunas UTC, que são indexáveis.
-- =====================================================================================

CREATE TABLE dbo.Tags (
    EpcId                   NVARCHAR(128) NOT NULL CONSTRAINT PK_Tags PRIMARY KEY,
    Status                  NVARCHAR(8)   NOT NULL CONSTRAINT CK_Tags_Status CHECK (Status IN (N'DENTRO', N'FORA')),
    UltimaLeitura           DATETIME2(3)  NOT NULL,
    UltimaAtualizacaoStatus DATETIME2(3)  NOT NULL,
    UltimaAntenaId          INT           NULL,
    UltimaRssi              FLOAT         NULL
);

CREATE TABLE dbo.Eventos (
    Id        BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Eventos PRIMARY KEY,
    EpcId     NVARCHAR(128) NOT NULL CONSTRAINT FK_Eventos_Tags REFERENCES dbo.Tags (EpcId),
    Modo      NVARCHAR(8)   NOT NULL CONSTRAINT CK_Eventos_Modo CHECK (Modo IN (N'ENTRADA', N'SAIDA')),
    Tipo      NVARCHAR(20)  NOT NULL CONSTRAINT CK_Eventos_Tipo CHECK (Tipo IN (N'CRIACAO', N'MUDANCA_STATUS', N'SAIDA_SEM_ENTRADA')),
    [Timestamp] DATETIME2(3) NOT NULL,
    AntenaId  INT           NOT NULL,
    Rssi      FLOAT         NOT NULL
);

CREATE TABLE dbo.TagCadastro (
    EpcId        NVARCHAR(128) NOT NULL CONSTRAINT PK_TagCadastro PRIMARY KEY,
    Descricao    NVARCHAR(200) NOT NULL CONSTRAINT CK_TagCadastro_Descricao CHECK (LEN(Descricao) >= 1),
    Sku          NVARCHAR(64)  NULL,
    Observacao   NVARCHAR(500) NULL,
    Categoria    NVARCHAR(64)  NULL,
    Unidade      NVARCHAR(16)  NULL,
    Localizacao  NVARCHAR(100) NULL,
    CadastradoEm DATETIME2(3)  NOT NULL,
    AtualizadoEm DATETIME2(3)  NOT NULL
);

CREATE TABLE dbo.Passagens (
    Id                 BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Passagens PRIMARY KEY,
    Modo               NVARCHAR(8)   NOT NULL CONSTRAINT CK_Passagens_Modo CHECK (Modo IN (N'ENTRADA', N'SAIDA')),
    InicioUtc          DATETIME2(3)  NOT NULL,
    FimUtc             DATETIME2(3)  NOT NULL,
    Tags               INT           NOT NULL,
    Leituras           INT           NOT NULL,
    Rodadas            INT           NOT NULL,
    EstimativaPerdidas FLOAT         NOT NULL,
    Veredito           NVARCHAR(20)  NOT NULL
        CONSTRAINT CK_Passagens_Veredito CHECK (Veredito IN (N'CONFIAVEL', N'ATENCAO', N'NAO_CONVERGIU', N'DADOS_INCOMPLETOS')),
    MotivoFechamento   NVARCHAR(400) NULL,
    Problemas          NVARCHAR(MAX) NULL,
    CONSTRAINT CK_Passagens_Periodo CHECK (FimUtc >= InicioUtc)
);

CREATE INDEX IX_TagCadastro_Sku ON dbo.TagCadastro (Sku);
CREATE INDEX IX_Tags_Status ON dbo.Tags (Status);
CREATE INDEX IX_Tags_UltimaLeitura ON dbo.Tags (UltimaLeitura);
CREATE INDEX IX_Eventos_EpcId_Timestamp ON dbo.Eventos (EpcId, [Timestamp]);
CREATE INDEX IX_Eventos_Timestamp ON dbo.Eventos ([Timestamp]);
CREATE INDEX IX_Eventos_Tipo ON dbo.Eventos (Tipo);
CREATE INDEX IX_Eventos_Modo_Timestamp ON dbo.Eventos (Modo, [Timestamp]);
CREATE INDEX IX_Passagens_Modo_Inicio ON dbo.Passagens (Modo, InicioUtc) INCLUDE (FimUtc);
GO

-- Cadastro + status atual + última detecção. Tag cadastrada e nunca lida: Status/UltimaDeteccao NULL.
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
    CONVERT(DATETIME2(3), t.UltimaLeitura AT TIME ZONE 'UTC' AT TIME ZONE 'E. South America Standard Time') AS UltimaDeteccaoLocal
FROM dbo.TagCadastro c
LEFT JOIN dbo.Tags t ON t.EpcId = c.EpcId;
GO

-- Eventos + item do cadastro + passagem (por modo e janela de tempo) + flag de anomalia.
CREATE VIEW dbo.Movimentacoes AS
SELECT
    e.Id,
    e.EpcId,
    c.Descricao,
    c.Sku,
    e.Modo        AS Tipo,
    e.Tipo        AS Evento,
    e.[Timestamp] AS DataHora,
    e.AntenaId,
    e.Rssi,
    p.PassagemId,
    CAST(CASE WHEN e.Tipo = N'SAIDA_SEM_ENTRADA' THEN 1 ELSE 0 END AS BIT) AS Anomalia,
    CONVERT(DATETIME2(3), e.[Timestamp] AT TIME ZONE 'UTC' AT TIME ZONE 'E. South America Standard Time') AS DataHoraLocal
FROM dbo.Eventos e
LEFT JOIN dbo.TagCadastro c ON c.EpcId = e.EpcId
OUTER APPLY (
    SELECT TOP (1) pa.Id AS PassagemId
    FROM dbo.Passagens pa
    WHERE pa.Modo = e.Modo AND pa.InicioUtc <= e.[Timestamp] AND pa.FimUtc >= e.[Timestamp]
    ORDER BY pa.InicioUtc DESC
) p;
