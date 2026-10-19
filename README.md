<p align="center"><img src="assets/icon-256.png" width="96" alt=""></p>

# SLT Drop Local QRCode

Aplicativo para Windows que recebe arquivos do celular pela rede Wi-Fi/LAN.
O PC mostra um QR Code; o celular escaneia, abre uma página no navegador e envia
os arquivos direto para a pasta escolhida no PC. Sem cabo, sem internet e sem
instalar nada no celular (funciona em Android e iPhone).

## Funcionalidades

- **Pasta escolhida ao abrir:** a primeira coisa que o programa faz é perguntar onde salvar.
- **Qualquer arquivo, de qualquer tamanho:** o envio vai em blocos de 8 MB gravados
  direto no disco; nada é carregado inteiro na memória. Testado acima de 4 GB.
- **Porcentagem, velocidade e tempo estimado** por arquivo e no total, no celular e no PC.
- **Pausar, retomar e cancelar** cada arquivo ou todos de uma vez. Se a rede cair,
  o celular reconecta e continua de onde parou. Cancelado é apagado do PC.
- **QR Code de uso único:** cada sessão tem um token aleatório de 256 bits. A sessão
  termina pelo botão **Encerrar sessão** no celular, pelo botão **Gerar novo QR Code**
  no PC ou depois de **15 minutos sem atividade**. Na hora, um QR Code novo aparece e o
  link antigo nunca mais funciona.
- **Um aparelho por sessão:** o primeiro celular que abre o link fica com a sessão.
- **Nunca sobrescreve:** `foto.jpg` repetida vira `foto (2).jpg`. Nomes perigosos
  (`../`, `CON`, caracteres inválidos) são corrigidos.
- **Instalador completo:** um `Setup.exe` com tudo dentro (runtime do .NET, Windows
  Forms, servidor HTTP e gerador de QR Code). Cria a regra no Firewall do Windows e a
  remove na desinstalação.

## Instalação

Baixe `SLT-Drop-Local-QRCode-Setup-<versão>.exe` (artefato **instalador** do CI ou
gerado localmente) e execute. É preciso ser administrador: só assim o instalador
consegue criar a regra do firewall que deixa o celular alcançar o PC.

## Como usar

1. Abra o **SLT Drop Local QRCode** e escolha a pasta de destino.
2. Conecte o celular à mesma rede do PC.
3. Escaneie o QR Code com a câmera do celular e toque no link.
4. Toque em **Escolher arquivos**.
5. Ao terminar, toque em **Encerrar sessão**.

Se o celular não abrir o link: confira se os dois estão na mesma rede (redes de
convidados isolam os aparelhos), desligue os dados móveis do celular e, na janela do
programa, escolha em **Rede usada no QR Code** a placa ligada ao roteador.

### Opções de linha de comando

| Opção | Efeito |
|---|---|
| `--pasta "D:\Recebidos"` | Usa a pasta sem abrir a janela de escolha |
| `--porta 8770` | Porta preferida (padrão 8770; se ocupada, tenta as seguintes) |
| `--inatividade-segundos 900` | Tempo sem atividade até a sessão expirar (padrão 15 min) |

## Desenvolvimento

Requisitos: .NET SDK 10 e Inno Setup 6 (`winget install --id JRSoftware.InnoSetup`).

```powershell
dotnet test SltDropLocal.slnx                # testes
.\scripts\gerar-instalador.ps1               # testes + publicação + instalador em entrega\
```

O teste com arquivo acima de 4 GB é lento e só roda com `SLTDROP_TESTE_GRANDE=1`.

| Pasta | Conteúdo |
|---|---|
| `src/SltDropLocal.Core` | Sessões, recebimento em blocos, servidor HTTP (Kestrel), página do celular, QR Code |
| `src/SltDropLocal.App` | Aplicativo Windows Forms |
| `tests/SltDropLocal.Tests` | Testes de unidade e de integração com servidor HTTP real |
| `instalador/` | Script do Inno Setup e LEIA-ME instalado junto |
| `scripts/` | Geração do instalador e do ícone |

A versão fica em `Directory.Build.props`; o instalador e o rodapé do programa leem de lá.

### API usada pela página do celular

Tudo fica sob `/s/{token}`. Sem o token da sessão atual não há página nem API.

| Método | Caminho | Uso |
|---|---|---|
| `GET` | `/s/{t}` | Página de envio |
| `GET` | `/s/{t}/api/estado` | Tempo até expirar, nome do PC, pasta, tamanho do bloco (não conta como atividade) |
| `POST` | `/s/{t}/api/envios` | Começa um arquivo `{nome, tamanho}` |
| `GET` | `/s/{t}/api/envios/{id}` | De onde continuar |
| `PUT` | `/s/{t}/api/envios/{id}?posicao=N` | Um bloco de bytes |
| `POST` | `/s/{t}/api/envios/{id}/pausa` | Avisa o PC da pausa |
| `DELETE` | `/s/{t}/api/envios/{id}` | Cancela e apaga o que chegou |
| `POST` | `/s/{t}/api/encerrar` | Encerra a sessão |

Sessão expirada responde `410`, link inexistente `404`, sessão de outro aparelho `403`.

---

Smells Like Tech
