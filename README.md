# Alexa Widget para Windows

Controle a sua Alexa pelo PC: um **widget** sempre à mão no canto da tela, ou uma **janela de app** completa.
Fale pelo microfone do PC, ligue e desligue dispositivos da casa, veja e crie alarmes e lembretes, crie rotinas,
ative skills e use o Echo como caixa de som do computador.

> **Projeto não oficial.** Não tem relação com a Amazon. Ele usa os mesmos serviços de nuvem que o app Alexa
> do celular usa, descobertos por engenharia reversa do app Android. A Amazon pode mudar esses serviços a
> qualquer momento e alguma função pode parar de funcionar.

![Todas as telas do widget](docs/resumo-widget.png)

## Download

1. Abra a página de **[Releases](../../releases/latest)** e baixe o **`AlexaWidget.exe`**.
2. Dê dois cliques nele. Não precisa instalar nada.

Na primeira vez, o Windows pode mostrar **"O Windows protegeu o computador"**, porque o arquivo não tem
assinatura digital. Clique em **Mais informações → Executar assim mesmo**.

### Requisitos

- Windows 10 ou 11, 64 bits.
- **Microsoft Edge WebView2 Runtime**. Ele já vem no Windows 11 e na maioria dos Windows 10 atualizados.
  Se o widget abrir em branco, instale pelo [site da Microsoft](https://developer.microsoft.com/microsoft-edge/webview2/).
- Uma conta Amazon com a Alexa configurada (por padrão, a loja **amazon.com.br**; veja
  [Outros países](#outros-países)).

## Primeiros passos

1. Abra o `AlexaWidget.exe`. O widget aparece no canto inferior direito da tela.
2. Clique em **Entrar com a Amazon** e entre na sua conta na página oficial da Amazon.
   Na página de login, escolha **"Login — Já é cliente?"**, não "Criar conta".
   - Se a janela de login ficar em branco, use **Entrar pelo navegador**: o login abre no seu navegador e,
     no final, você só precisa **copiar** o endereço da barra (começa com `https://www.amazon.com.br/ap/maplanding`).
     O widget percebe sozinho e termina o login (se não perceber, cole o endereço no campo do widget).
     O pedido de login vale por 15 minutos, mesmo que o widget seja reiniciado nesse meio-tempo.
   - Se a Amazon cancelar ou recusar o login, o widget mostra o motivo.
3. Pronto. Escolha o Echo no topo da tela **Início** e envie um comando.

O PC passa a aparecer na sua conta Amazon como **"Alexa Widget (PC)"**, em *Gerenciar conteúdo e dispositivos*.
**Ajustes → Conta → Sair da conta Amazon** remove esse registro da conta também.

### Widget, janela e bandeja

- **⤢** alterna entre o widget e a janela de app (com barra lateral).
- **–** minimiza para a barra de tarefas. **✕** esconde na bandeja do sistema (perto do relógio).
- Clique no ícone da bandeja para mostrar de novo. Abrir o `AlexaWidget.exe` outra vez também traz a janela.
- Clique com o botão direito no ícone da bandeja para **Iniciar com o Windows** ou **Sair**.

![Modo janela](docs/resumo-janela.png)

## O que dá para fazer

| Tela | Funções |
|---|---|
| **Início** | Digitar ou **falar** um comando (🎤) para o Echo escolhido · comandos rápidos editáveis (✎) · dispositivos fixados · recentes |
| **Casa** | Ligar e desligar dispositivos · botão **…** com opções avançadas (bateria, modos, brilho, cor, tom de branco, comando livre, fixar no Início, atalho) |
| **Alarmes** | Ver, **criar** e **excluir** alarmes, timers e lembretes, inclusive repetidos (diário, dias úteis, fins de semana) |
| **Rotinas** | **Rotinas do widget**: disparadas por uma frase, por horário ou pelo ▶ · rotinas criadas no app do celular: executar, ativar/desativar, excluir |
| **Mais → Skills** | Procurar e ativar skills na loja oficial (abre já conectada à sua conta) ou ativar pelo Echo |
| **Mais → Música** | Ver os serviços padrão da sua Alexa · conectar Spotify, Deezer, Apple Music · escolher o serviço usado nos pedidos "tocar…" feitos pelo widget |
| **Mais → Echos** | Seus Echos · **Caixa do PC**: pareia o Echo por Bluetooth para o som do Windows sair nele |
| **Mais → Ajustes** | Atalhos de teclado, botão do mouse para falar, correções de frases, sair da conta |

### Falar com a Alexa pelo PC

- Clique no 🎤 (ou use o atalho **Ctrl+Alt+A**, que funciona em qualquer programa), espere o bipe e fale.
  A gravação para sozinha quando você faz uma pausa, ou quando você clica de novo.
- A fala é transcrita **no seu próprio PC**, sem enviar áudio para a internet, com o modelo de voz Whisper.
  Na primeira vez, o widget baixa o modelo (~180 MB).
- A Alexa responde em voz no Echo escolhido.

### Atalhos

Em **Ajustes → Atalhos** (ou no ✎ dos comandos rápidos → **⌨ Atalhos**):

- um atalho para falar com a Alexa (padrão **Ctrl+Alt+A**);
- um botão extra do mouse para falar (botão lateral "voltar", "avançar" ou o do meio);
- um atalho para **cada comando rápido** e para **ligar/desligar cada dispositivo** da casa.

Todos funcionam com o widget escondido. Se outro programa já usa a combinação, o widget avisa.

### Frases que a Alexa entende

O texto enviado à Alexa é ajustado automaticamente antes de sair:

- verbos no infinitivo: "**liga** o robô" → "**ligar** o robô", "apaga", "toca", "aumenta"…;
- nome exato do dispositivo: "robo aspirador" → "Robô Aspirador de Pó 100";
- **ligar/desligar um dispositivo da casa é feito direto**, sem depender da interpretação da Alexa.

Em **Ajustes → Frases para a Alexa** você cadastra correções suas ("quando eu disser *limpa a casa*, enviar
*iniciar limpeza*") e testa qualquer frase sem enviar nada.

## Privacidade

- O widget guarda só o token de acesso à sua conta, **criptografado com o seu usuário do Windows** (DPAPI).
  Sua senha não passa pelo widget: o login acontece na página da Amazon.
- Tudo fica em `%LOCALAPPDATA%\AlexaWidget` no seu PC: login, preferências, rotinas do widget e o modelo de voz.
- O áudio do microfone é transcrito localmente e não é gravado em arquivo.
- O widget só fala com os servidores da Amazon e, uma única vez, com o Hugging Face para baixar o modelo de voz.

## Limitações conhecidas

- **Rotinas na conta Amazon**: dá para executar, ativar/desativar e excluir as rotinas criadas no celular,
  mas não criar novas (o serviço recusa). As **rotinas do widget** funcionam só com o PC ligado e só pela
  frase dita ou digitada no widget, não pela frase dita direto ao Echo.
- **Serviço de música padrão** da Alexa só pode ser trocado no app do celular. O widget pode mandar seus pedidos
  "tocar…" para o serviço que você escolher.
- **YouTube Music** não funciona com a Alexa (não existe skill oficial).
- Configurar um **Echo novo** ainda exige o app no celular.
- Se aparecer **"A Amazon está limitando a consulta de alarmes"**, espere alguns minutos: o widget tenta de novo sozinho.

## Desinstalar

1. Em **Ajustes → Conta**, clique em **Sair da conta Amazon** (remove "Alexa Widget (PC)" da sua conta).
2. Clique com o botão direito no ícone da bandeja → **Sair**.
3. Apague o `AlexaWidget.exe` e a pasta `%LOCALAPPDATA%\AlexaWidget`.
4. Se ainda sobrar algum **"Alexa Widget (PC)"** em *Gerenciar conteúdo e dispositivos* (por exemplo, de versões
   anteriores à 1.0.1), remova por lá.

## Outros países

Edite `%LOCALAPPDATA%\AlexaWidget\settings.json` com a loja e o idioma da sua conta e reabra o widget. Exemplo:

```json
{ "AmazonDomain": "amazon.com", "Locale": "en-US" }
```

## Compilar a partir do código

Precisa do [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet publish AlexaWidget.csproj -c Release -r win-x64 -o release
```

O resultado é um único `release\AlexaWidget.exe`, com o .NET e a interface embutidos.
Para desenvolver, `dotnet run` abre o widget direto do código.

A interface (`wwwroot/`) também abre num navegador comum com dados de exemplo (`mock.js`):
sirva a pasta `wwwroot` (ex.: `python -m http.server`) e acesse `index.html` (`?mode=app` para o modo janela).

## Licença

[MIT](LICENSE): use, copie, modifique e redistribua à vontade, mantendo o aviso de copyright.
O software é fornecido "como está", sem garantias.

<details>
<summary>Como funciona (detalhes técnicos)</summary>

- Cliente Windows em .NET 10 (WinForms + WebView2); a interface é HTML/CSS/JS em `wwwroot/`.
- **Login**: OAuth com PKCE na página de login da Amazon, registrando o PC com a identidade do app Android
  (`MAPDeviceType` `A2TF17PFR55MTB`, handle `amzn_dp_project_dee_android_*`). O refresh token vira os cookies
  da sessão web, usados nas APIs `alexa.amazon.*`.
- **Comandos**: `/api/behaviors/preview` com `Alexa.TextCommand` (e `Alexa.Speak` para "Alexa diz").
- **Casa**: GraphQL em `/nexus/v1/graphql` (`listEndpoints`, `setEndpointFeatures`).
- **Alarmes e lembretes**: `/api/notifications` (listar), `PUT /api/notifications/create{Tipo}`, `DELETE /api/notifications/{id}`.
- **Rotinas**: `/api/behaviors/v2/automations` (listar), `/api/behaviors/automations/{id}` (alterar/excluir).
- **Bluetooth**: `/api/bluetooth/enable-pairing-mode/...` + pareamento pelo Windows (`Windows.Devices.Enumeration`).
- **Voz**: NAudio para o microfone e Whisper.net (`ggml-small-q5_1`) para a transcrição local.
- **Callback do login**: só o endereço exato `https://www.amazon.<loja>/ap/maplanding` é aceito (esquema, servidor
  e caminho, também dentro de frames); cancelamento e erros da Amazon viram mensagem; o token só é gravado depois
  que a sessão web funciona; "Sair" chama `/auth/deregister`.
- Diagnóstico: `AlexaWidget.exe --login-selftest saida.txt`, `--dump-notifications saida.json`,
  `--dump-features saida.txt`, `--transcribe audio16k.wav saida.txt`, `--listen-test saida.txt`.

</details>
