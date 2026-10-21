namespace SltDropLocal.Core.Sessoes;

/// <summary>
/// Lembra os códigos de acesso já usados para que nenhum se repita, nem entre
/// execuções do programa. Com arquivo, sobrevive a reinícios; sem arquivo, vale
/// só para a execução atual (testes).
/// </summary>
public sealed class HistoricoDeCodigos
{
    /// <summary>
    /// Quantos códigos o arquivo guarda. Com trilhões de códigos possíveis de 8
    /// caracteres, repetir é praticamente impossível de qualquer jeito; o histórico
    /// torna isso garantido para os últimos 100 mil e mantém o arquivo pequeno.
    /// </summary>
    public const int Limite = 100_000;

    private readonly string? _arquivo;
    private readonly HashSet<string> _usados = [];
    private readonly Queue<string> _ordem = new();
    private readonly Lock _trava = new();

    public HistoricoDeCodigos(string? arquivo = null)
    {
        _arquivo = arquivo;
        if (_arquivo is null || !File.Exists(_arquivo)) return;
        try
        {
            foreach (var linha in File.ReadLines(_arquivo))
                if (linha.Length > 0 && _usados.Add(linha)) _ordem.Enqueue(linha);
            Aparar();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Histórico ilegível: segue só com a memória desta execução.
        }
    }

    public int Quantidade
    {
        get { lock (_trava) return _usados.Count; }
    }

    /// <summary>Reserva o código se ele nunca foi usado. Devolve false se já foi.</summary>
    public bool Reservar(string codigo)
    {
        lock (_trava)
        {
            if (!_usados.Add(codigo)) return false;
            _ordem.Enqueue(codigo);
            var aparou = Aparar();
            Gravar(codigo, reescrever: aparou);
            return true;
        }
    }

    private bool Aparar()
    {
        var aparou = false;
        while (_ordem.Count > Limite)
        {
            _usados.Remove(_ordem.Dequeue());
            aparou = true;
        }
        return aparou;
    }

    private void Gravar(string codigo, bool reescrever)
    {
        if (_arquivo is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_arquivo))!);
            if (reescrever) File.WriteAllLines(_arquivo, _ordem);
            else File.AppendAllText(_arquivo, codigo + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sem gravar, a garantia continua valendo para esta execução.
        }
    }
}
