# Network Backup Tool

Copia arquivos do PC de um cliente para outro PC da rede **sem compartilhamento do Windows**
(sem SMB, sem pasta compartilhada, sem usuário/senha, sem grupo de trabalho). É um único `.exe`
que roda em qualquer Windows 10/11 64 bits, sem instalar nada — dá para levar no pendrive.

## Como usar

**No seu PC (quem recebe)**
1. Abra `NetBackupTool.exe` → aba **Receber** → **Iniciar recepção**.
2. Na primeira vez o Windows pergunta sobre o firewall: permita. Se a rede estiver como
   "Pública" e nada conectar, clique em **Liberar no firewall** (pede admin uma vez).
3. Anote o **código de 6 dígitos** mostrado.

**No PC do cliente (quem envia)**
1. Abra o mesmo `NetBackupTool.exe` → aba **Enviar**.
2. Adicione o que copiar: **Pastas do usuário** (Área de Trabalho, Documentos, Imagens,
   Músicas, Vídeos, Favoritos, Downloads), **Adicionar pasta**, ou arraste pastas para a lista.
3. **Procurar na rede** (acha seu PC sozinho) ou digite o IP. Digite o código. **Enviar**.

Os arquivos chegam em `<pasta de destino>\<nome do PC do cliente>\C\Users\...`, espelhando o
caminho original. Ao fim, um `_NBT_relatorio_<data>.txt` fica junto, com o que foi copiado e o que falhou.

Atalho útil no seu PC: `NetBackupTool.exe receber` abre já recebendo.

## Por que é rápido

- **Várias conexões TCP em paralelo** (padrão 4, até 16): arquivos grandes são fatiados em
  blocos de 8 MB distribuídos entre as conexões; arquivos pequenos são processados em paralelo,
  o que esconde a latência de abrir/criar cada arquivo.
- Protocolo enxuto, sem confirmação por arquivo: os dados fluem direto, com buffers grandes.
- Medido nesta máquina: ~1 GB/s (≈9 Gbps) pela pilha de rede local. Em rede de 1 Gbps o limite
  passa a ser o cabo (~110 MB/s) ou o disco do cliente (HD mecânico costuma ser o gargalo).
- Para muitos arquivos pequenos, aumentar para 8–16 conexões costuma ajudar.

## Segurança e confiabilidade

- Só conecta quem sabe o código (HMAC-SHA256 com desafio aleatório; o código não trafega).
  Após 5 códigos errados, o código é trocado automaticamente.
- Cada bloco é conferido com xxHash3; arquivo com bloco corrompido é descartado e listado como falha.
- O receptor nunca grava fora da pasta de destino (caminhos com `..` ou absolutos são recusados).
- Verifica espaço livre antes de começar.
- **Retomada**: enviar de novo pula o que já chegou igual (mesmo tamanho e data). Se cair a rede
  ou cancelar, basta enviar de novo.
- Lê arquivos abertos por outros programas quando o Windows permite (compartilhamento de leitura).
- **Arquivos em uso com exclusividade** (`.pst` com Outlook aberto, bancos de dados): rodando como
  administrador, o programa cria uma **Shadow Copy (VSS)** do disco só quando encontra o primeiro
  arquivo assim, lê esses arquivos dela (um retrato consistente daquele instante) e apaga a Shadow
  Copy ao terminar. Se o programa for fechado à força, a próxima execução como admin apaga a sobra.
  Sem admin, esses arquivos aparecem como falha com a dica; na aba Enviar há o link
  **Reabrir como administrador**, que mantém a lista de pastas e as "Pastas do usuário" do usuário
  original, mesmo que no UAC você entre com outra conta.
- Pula junções do sistema ("Dados de Aplicativos", "Meus Documentos" antigos) e arquivos do
  OneDrive que só existem na nuvem (avisa a quantidade).
- O tráfego **não é criptografado** (pensado para rede local de confiança; criptografia custaria velocidade).

Portas: TCP 50505 (dados) e UDP 50506 (descoberta), só no PC que recebe.

## Compilar

Requer .NET SDK 10.

```bash
dotnet publish src/NetBackupTool -c Release -o publish
```

Gera `publish/NetBackupTool.exe` (autocontido, ~49 MB).

Teste de ponta a ponta (gera dados temporários, envia por loopback, confere hash e data, testa
código errado, arquivo bloqueado, exclusões, retomada e caminhos maliciosos):

```bash
dotnet run -c Release --project tests/Nbt.Smoke
```

Rodado num terminal **como administrador**, o mesmo teste confere que o arquivo bloqueado chega
íntegro via Shadow Copy e que a Shadow Copy é apagada no fim.

## Estrutura

- `src/Nbt.Core` — protocolo, varredura (`Manifest`), `Sender`, `Receiver`, `Discovery`, `ShadowCopies` (VSS).
- `src/NetBackupTool` — interface WinForms.
- `tests/Nbt.Smoke` — teste de ponta a ponta.
