using System.Data;
using Dapper;

namespace KrfReader.Infrastructure.Persistence;

/// <summary>
/// Parâmetro DATETIME2 explícito para o Dapper. Sem isso o Dapper manda <see cref="DateTime"/> como DATETIME
/// (precisão de 3,33 ms), e a comparação com colunas DATETIME2(3) erra a ordem de leituras no mesmo intervalo.
/// Local ao SQL Server: não registra TypeMap global.
/// Classe, não struct: o IL gerado pelo Dapper para ICustomQueryParameter não faz box de value type (segfault).
/// </summary>
internal sealed class SqlDateTime2(DateTime value) : SqlMapper.ICustomQueryParameter
{
    public DateTime Value { get; } = DbTimestamp.TruncateToMillisUtc(value);

    public void AddParameter(IDbCommand command, string name)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.DateTime2;
        parameter.Value = Value;
        command.Parameters.Add(parameter);
    }
}
