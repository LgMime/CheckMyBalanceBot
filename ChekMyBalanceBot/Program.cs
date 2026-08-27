using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot.Polling;
using System.Text.Json;

// Configuration
const string botToken = "Your bot`s token";
const string bankApiUrl = "https://api-open.ccp.dnb.no/v1/kronekort/balance";
const string storageFilePath = "users_storage.json"; // Имя файла для сохранения данных

// Загружаем сохраненные карты пользователей при старте (если файл существует)
var userCardNumbers = LoadUserData();

// Initialize Telegram Bot Client
var botClient = new TelegramBotClient(botToken);

// Polling options
var receiverOptions = new ReceiverOptions
{
    AllowedUpdates = Array.Empty<Telegram.Bot.Types.Enums.UpdateType>()
};

// Create HTTP client for API calls
var httpClient = new HttpClient();

// Start polling
using var cts = new CancellationTokenSource();

try
{
    await botClient.DeleteWebhookAsync(dropPendingUpdates: true);
    Console.WriteLine("Webhook deleted successfully.");
}
catch (Exception ex)
{
    Console.WriteLine($"Warning: Could not delete webhook: {ex.Message}");
}

var handler = new DefaultUpdateHandler(ProcessUpdate, ProcessError);
Console.WriteLine("Bot started. Data loaded. Press any key to stop.");
botClient.StartReceiving(handler, receiverOptions, cancellationToken: cts.Token);
Console.ReadKey();
cts.Cancel();

// Update handler
async Task ProcessUpdate(ITelegramBotClient client, Update update, CancellationToken cancellationToken)
{
    try
    {
        if (update.Type == Telegram.Bot.Types.Enums.UpdateType.Message)
        {
            var message = update.Message;
            if (message?.Text == null)
                return;

            var userId = message.From!.Id;
            var chatId = message.Chat.Id;
            var text = message.Text.Trim();

            // Check if user has already provided card number
            if (!userCardNumbers.ContainsKey(userId))
            {
                if (text.Length == 12 && text.All(char.IsDigit))
                {
                    var truncatedCardNumber = text.Substring(0, 11);

                    // Сохраняем в память и сразу дублируем в файл
                    userCardNumbers[userId] = truncatedCardNumber;
                    SaveUserData(userCardNumbers);

                    var replyMarkup = new ReplyKeyboardMarkup(new[]
                    {
                        new KeyboardButton[] { new KeyboardButton("Balance") }
                    })
                    {
                        ResizeKeyboard = true,
                        OneTimeKeyboard = false
                    };

                    await client.SendTextMessageAsync(
                        chatId: chatId,
                        text: $"✅ Card number saved! (Last digit removed)\n\nYour stored card: {truncatedCardNumber}",
                        replyMarkup: replyMarkup,
                        cancellationToken: cancellationToken
                    );
                }
                else
                {
                    await client.SendTextMessageAsync(
                        chatId: chatId,
                        text: "❌ Please send exactly 12 digits for your card number.",
                        cancellationToken: cancellationToken
                    );
                }
            }
            else
            {
                if (text == "Balance")
                {
                    var cardNumber = userCardNumbers[userId];
                    await SendBalanceRequest(client, chatId, cardNumber, cancellationToken);
                }
                else if (text.Length == 12 && text.All(char.IsDigit))
                {
                    var truncatedCardNumber = text.Substring(0, 11);

                    // Обновляем карту в памяти и перезаписываем файл
                    userCardNumbers[userId] = truncatedCardNumber;
                    SaveUserData(userCardNumbers);

                    await client.SendTextMessageAsync(
                        chatId: chatId,
                        text: $"✅ Card number updated! (Last digit removed)\n\nYour stored card: {truncatedCardNumber}",
                        cancellationToken: cancellationToken
                    );
                }
                else
                {
                    await client.SendTextMessageAsync(
                        chatId: chatId,
                        text: "ℹ️ Press the Balance button to check your balance or send a new 12-digit card number.",
                        cancellationToken: cancellationToken
                    );
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error processing update: {ex.Message}");
    }
}

// Error handler
async Task ProcessError(ITelegramBotClient client, Exception exception, CancellationToken cancellationToken)
{
    Console.WriteLine($"Telegram polling error: {exception.Message}");
    await Task.Delay(1000, cancellationToken);
}

// Send balance request to bank API
async Task SendBalanceRequest(ITelegramBotClient client, long chatId, string cardNumber, CancellationToken cancellationToken)
{
    try
    {
        await client.SendTextMessageAsync(
            chatId: chatId,
            text: "⏳ Checking balance...",
            cancellationToken: cancellationToken
        );

        var request = new HttpRequestMessage(HttpMethod.Post, bankApiUrl);

        // Идеальная маскировка под Оперу и обход шлюза
        request.Headers.Add("Origin", "https://www.dnb.no");
        request.Headers.Add("Referer", "https://www.dnb.no/");
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36 OPR/131.0.0.0");
        request.Headers.Add("X-DNBAPI-Trace-Id", Guid.NewGuid().ToString());
        request.Headers.Add("X-DNBAPI-Channel", "BMPULS");

        request.Headers.Add("Accept", "*/*");
        request.Headers.Add("Sec-Fetch-Site", "same-site");
        request.Headers.Add("Sec-Fetch-Mode", "cors");
        request.Headers.Add("Sec-Fetch-Dest", "empty");

        var payload = new { accountNumber = cardNumber };
        var jsonContent = new StringContent(
            JsonSerializer.Serialize(payload),
            System.Text.Encoding.UTF8,
            "application/json"
        );
        request.Content = jsonContent;

        var response = await httpClient.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.WriteLine($"API Response: {responseContent}");

            using var jsonDoc = JsonDocument.Parse(responseContent);
            var root = jsonDoc.RootElement;

            string balanceMessage = "💳 Balance Information:\n\n";

            if (root.TryGetProperty("balance", out var balanceElement))
            {
                if (balanceElement.ValueKind == JsonValueKind.Number)
                {
                    var balance = balanceElement.GetDecimal();
                    balanceMessage += $"Balance: {balance:N2} NOK";
                }
                else
                {
                    balanceMessage += $"Balance: {balanceElement.GetRawText()}";
                }
            }
            else
            {
                balanceMessage += "Full Response:\n" + FormatJson(responseContent);
            }

            await client.SendTextMessageAsync(
                chatId: chatId,
                text: balanceMessage,
                cancellationToken: cancellationToken
            );
        }
        else
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.WriteLine($"API Error: {response.StatusCode} - {errorContent}");

            await client.SendTextMessageAsync(
                chatId: chatId,
                text: $"❌ Error checking balance.\n\nStatus: {response.StatusCode}\nDetails: {errorContent}",
                cancellationToken: cancellationToken
            );
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in SendBalanceRequest: {ex.Message}");
        await client.SendTextMessageAsync(
            chatId: chatId,
            text: $"❌ An error occurred: {ex.Message}",
            cancellationToken: cancellationToken
        );
    }
}

// Вспомогательная функция форматирования JSON
string FormatJson(string json)
{
    try
    {
        using var jsonDoc = JsonDocument.Parse(json);
        var options = new JsonSerializerOptions { WriteIndented = true };
        return JsonSerializer.Serialize(jsonDoc.RootElement, options);
    }
    catch
    {
        return json;
    }
}

// Метод для ЗАГРУЗКИ данных из JSON-файла
Dictionary<long, string> LoadUserData()
{
    if (!System.IO.File.Exists(storageFilePath))
    {
        return new Dictionary<long, string>(); // Если файла нет, возвращаем пустой словарь
    }

    try
    {
        string json = System.IO.File.ReadAllText(storageFilePath);
        var data = JsonSerializer.Deserialize<Dictionary<long, string>>(json);
        return data ?? new Dictionary<long, string>();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Ошибка при загрузке базы данных: {ex.Message}");
        return new Dictionary<long, string>();
    }
}

// Метод для СОХРАНЕНИЯ данных в JSON-файл
void SaveUserData(Dictionary<long, string> data)
{
    try
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(data, options);
        System.IO.File.WriteAllText(storageFilePath, json);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Ошибка при сохранении базы данных: {ex.Message}");
    }
}