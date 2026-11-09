# Publicar na Play Store

Tudo o que o Play Console pede está nesta pasta. Ordem sugerida:

## 1. Conta e app

1. Crie a conta em <https://play.google.com/console> (taxa única de US$ 25; contas pessoais novas
   passam por verificação de identidade).
2. **Criar app** → nome *Network Backup Tool*, idioma padrão *Português (Brasil)*, tipo **App**,
   **Gratuito**.
3. Contas pessoais criadas recentemente precisam de um **teste fechado com pelo menos 12 testadores
   por 14 dias** antes de liberar a produção. Dá para convidar colegas técnicos pelo e-mail.

## 2. Assinatura

Em **Teste e lançamento → Configuração → Integridade do app → Assinatura de apps do Google Play**,
mantenha a opção padrão (o Google gera e guarda a chave de assinatura). A chave que está em
`Documentos\NetBackupTool-chave-android` vira a **chave de upload**: é com ela que o GitHub assina o
`.aab` que você envia. Não precisa enviar a chave ao Google.

> A versão da Play Store e a do GitHub ficam com assinaturas diferentes: quem trocar de uma para a
> outra precisa desinstalar antes.

## 3. Enviar o pacote

Baixe `NetBackupTool-android.aab` da [última release](https://github.com/leddzeppellin/network-backup-tool/releases/latest)
e envie numa faixa de teste (interno ou fechado). Esse pacote é a variante da loja: nele o botão
"Verificar atualizações" não aparece, porque a política do Google não permite atualizar por fora da loja.

A cada versão nova, o build do GitHub gera um `.aab` novo com número de versão maior.

## 4. Página da loja

- Textos em pt-BR, en-US e es-419: [pagina-do-app.md](pagina-do-app.md)
- Ícone 512×512: [imagens/icone-512.png](imagens/icone-512.png)
- Gráfico de destaque 1024×500: [imagens/banner-1024x500.png](imagens/banner-1024x500.png)
- Capturas do celular: [imagens/](imagens/) (`1-enviar` a `5-configuracoes`)

## 5. Conteúdo do app

Respostas e textos para os formulários (política de privacidade, permissão "Acesso a todos os
arquivos", segurança dos dados, classificação, público-alvo, anúncios):
[formularios.md](formularios.md).

O formulário da permissão costuma pedir um **vídeo curto** mostrando o uso; o roteiro está lá.

## 6. Revisão

Depois de enviar para revisão, o Google costuma responder em alguns dias. Se recusarem a permissão
"Acesso a todos os arquivos", o caminho é responder com o vídeo e o texto da declaração; se mesmo
assim negarem, a versão da loja teria de trocar a leitura de pastas pelo seletor do Android
(mais restrito), mantendo a versão completa no GitHub.
