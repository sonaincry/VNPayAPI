using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Imaging;
using QRCoder;
using System.Text.Json;
using System.Threading;
using System.Web;
using System.Windows;
namespace vnpay
{
    public sealed class VnpayQR : IDisposable
    {

        private bool _disposed = false;



        // initiate transaction - qr generate
        public string RequestSale(string UserId, string OrderCode, string TerminalCode, string merchantCode, Int64 TotalAmt, string sucessUrl, string cancelUrl, string merchantMethodCode, string MethodCode, string secretkey)
        {
            string lRequest = string.Empty;
            if (string.IsNullOrEmpty(UserId))
            {
                throw new ArgumentNullException(nameof(UserId));
            }
            if (string.IsNullOrEmpty(OrderCode))
            {
                {
                    throw new ArgumentException(nameof(OrderCode));
                }
            }

            if (string.IsNullOrEmpty(TerminalCode))
            {
                {
                    throw new ArgumentException(nameof(TerminalCode));
                }
            }
            if (string.IsNullOrEmpty(merchantCode))
            {
                {
                    throw new ArgumentException(nameof(merchantCode));
                }
            }
            if (string.IsNullOrEmpty(secretkey))
            {
                {
                    throw new ArgumentException(nameof(secretkey));
                }
            }
            string ClientCode = string.Empty;
            ClientCode = Guid.NewGuid().ToString();
            // Create the payload
            var payload = new
            {

                userId = UserId,
                checksum = GenerateChecksum(UserId, OrderCode, TerminalCode, merchantCode, TotalAmt, sucessUrl, cancelUrl, ClientCode, merchantMethodCode, MethodCode, secretkey),
                orderCode = OrderCode,
                payments = new
                {
                    qr = new
                    {
                        methodCode = "VNPAY_QRCODE",
                        merchantMethodCode = "",
                        clientTransactionCode = ClientCode,
                        amount = Convert.ToInt64(TotalAmt),
                        qrWidth = 275,
                        qrHeight = 275,
                        qrImageType = 0
                    }
                },
                cancelUrl = cancelUrl,
                successUrl = sucessUrl,
                terminalCode = TerminalCode,
                merchantCode = merchantCode,
                totalPaymentAmount = Convert.ToInt64(TotalAmt),
                expiredDate = DateTime.UtcNow.AddMinutes(15).ToString("yyMMddHHmm")
            };

            // Serialize the payload to JSON
            string jsonRequest = System.Text.Json.JsonSerializer.Serialize(payload);

            // Return the JSON request
            return jsonRequest;
        }
        //end
        // waiting for a repsonse from VNPay
        public string ResponseVNPAY(string orderCode, string transactionCode, string cardNumberFirst6Digits, string cardNumberLast4Digits, string extraData, string bankCode, string merchantMethodCode, string merchantCode, Int64 amount, string MethodCode, string partnerCode, string ipnkey)
        {
            string lRequest = string.Empty;
            if (string.IsNullOrEmpty(orderCode))
            {
                throw new ArgumentNullException(nameof(orderCode));
            }
            if (string.IsNullOrEmpty(transactionCode))
            {
                {
                    throw new ArgumentException(nameof(transactionCode));
                }
            }

            if (string.IsNullOrEmpty(merchantMethodCode))
            {
                {
                    throw new ArgumentException(nameof(merchantMethodCode));
                }
            }
            if (string.IsNullOrEmpty(cardNumberFirst6Digits))
            {
                {
                    throw new ArgumentException(nameof(cardNumberFirst6Digits));
                }
            }
            if (string.IsNullOrEmpty(cardNumberLast4Digits))
            {
                {
                    throw new ArgumentException(nameof(cardNumberLast4Digits));
                }
            }
            if (string.IsNullOrEmpty(ipnkey))
            {
                {
                    throw new ArgumentException(nameof(ipnkey));
                }
            }
            string ClientCode = string.Empty;
            ClientCode = Guid.NewGuid().ToString();
            // Create the ipn response
            var ipnload = new
            {

                merchantMethodCode = "",
                methodCode = "VNPAY_QRCODE",
                partnerCode = "",
                merchantCode = "",
                orderCode = "",
                amount = amount,
                realamount = amount,
                totalPaid = amount,
                clientTransactionCode = ClientCode,
                transactionCode = transactionCode,
                responseCode = "200",
                responseMessage = "Giao dịch thành công",
                partnerTransactionCode = partnerCode,
                bankCode = bankCode,
                cardNumberFirst6Digits = cardNumberFirst6Digits,
                cardNumberLast4Digits = cardNumberLast4Digits,
                extraData = "",
                checksum = GenerateIPNChecksum(orderCode, transactionCode, merchantMethodCode, amount, "200", ipnkey)
            };

            // Serialize the payload to JSON
            string jsonRequest = System.Text.Json.JsonSerializer.Serialize(ipnload);

            // Return the JSON request
            return jsonRequest;
        }
        //end
        //Post sale transaction
        public async Task<string> SendPostRequestSale(string jsonPayload, string endpoint)
        {

            using (var httpClient = new HttpClient())
            {
                // httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                //var content = new StringContent(jsonPayload);

                try
                {
                    HttpResponseMessage response = await httpClient.PostAsync(endpoint, content);

                    // Log or process the response code
                    Console.WriteLine($"Response Status Code: {(int)response.StatusCode} ({response.StatusCode})");

                    if (!response.IsSuccessStatusCode)
                    {
                        // Log or throw if the status code indicates an error
                        throw new HttpRequestException($"HTTP POST failed. Status Code: {(int)response.StatusCode}, Reason: {response.ReasonPhrase}");
                    }

                    // Read and return the response content as a string
                    return await response.Content.ReadAsStringAsync();
                }
                catch (HttpRequestException ex)
                {
                    // Log the error message
                    Console.WriteLine($"HTTP Request Exception: {ex.Message}");
                    throw;
                }
                catch (Exception ex)
                {
                    // Log other potential errors
                    Console.WriteLine($"Unexpected Exception: {ex.Message}");
                    throw;
                }
            }
        }
        //end
        //Get order transaction
        public async Task<string> WaitForGetOrderResponse(string orderCode, string paymentRequestId, string terminalCode, string merchantCode, string endpoint, string querykey, CancellationToken cancellationToken)
        {
            string response = string.Empty;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    response = await GetOrderRequestSale(orderCode, terminalCode, merchantCode, paymentRequestId, querykey, endpoint);

                    using (JsonDocument document = JsonDocument.Parse(response))
                    {
                        if (document.RootElement.TryGetProperty("data", out var dataElement))
                        {
                            if (dataElement.TryGetProperty("transactions", out var transactionsElement) &&
                                transactionsElement.ValueKind == JsonValueKind.Array &&
                                transactionsElement.GetArrayLength() > 0)
                            {
                                var transaction = transactionsElement[0];
                                string status = transaction.GetProperty("status").GetString();

                                Console.WriteLine($"Current status: {status}");

                                if (status == "SUCCESS")
                                {
                                    Console.WriteLine("Success");
                                    break;
                                }
                                else if (status == "FAILED" || status == "CANCELLED")
                                {
                                    Console.WriteLine("Fail");
                                    break;
                                }
                                else
                                {
                                    // Still processing, keep waiting
                                    await Task.Delay(5000, cancellationToken); // Poll every 5 seconds
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine("No 'data' in response — check JSON format.");
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Payment waiting canceled.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred: {ex.Message}");
            }

            return response;
        }

        public async Task<string> GetOrderRequestSale(string orderCode, string TerminalCode, string merchantCode, string paymentRequestID, string queryKey, string endpoint)
        {
            string url = string.Empty;
            string checksum = string.Empty;
            if (string.IsNullOrEmpty(orderCode) || string.IsNullOrEmpty(TerminalCode) || string.IsNullOrEmpty(merchantCode) || string.IsNullOrEmpty(queryKey))
            {
                throw new ArgumentException("Required parameter is missing.");
            }
            using (var httpClient = new HttpClient())
            {

                checksum = GenerateGetOrderChecksum(orderCode, TerminalCode, merchantCode, paymentRequestID, queryKey);
                url = $"{endpoint}?merchantCode={merchantCode}&terminalCode={TerminalCode}&paymentRequestId={paymentRequestID}&checksum={checksum}&orderCode={orderCode}";
                try
                {
                    HttpResponseMessage response = await httpClient.GetAsync(url);

                    // Log or process the response code
                    Console.WriteLine($"Response Status Code: {(int)response.StatusCode} ({response.StatusCode})");

                    if (!response.IsSuccessStatusCode)
                    {
                        // Log or throw if the status code indicates an error
                        throw new HttpRequestException($"HTTP POST failed. Status Code: {(int)response.StatusCode}, Reason: {response.ReasonPhrase}");
                    }

                    // Read and return the response content as a string
                    return await response.Content.ReadAsStringAsync();
                }
                catch (HttpRequestException ex)
                {
                    // Log the error message
                    Console.WriteLine($"HTTP Request Exception: {ex.Message}");
                    throw;
                }
                catch (Exception ex)
                {
                    // Log other potential errors
                    Console.WriteLine($"Unexpected Exception: {ex.Message}");
                    throw;
                }
            }
        }
        //end
        // generate a checksum
        private string GenerateChecksum(string userId, string orderCode, string TerminalCode, string merchantCode, Int64 TotalAmt, string sucessUrl, string cancelUrl, string ClientTransCode, string merchantMethodCode, string MethodCode, string secretKey)
        {
            if (string.IsNullOrEmpty(userId))
                throw new ArgumentNullException(nameof(userId), "User ID cannot be null or empty.");
            if (string.IsNullOrEmpty(orderCode))
                throw new ArgumentNullException(nameof(orderCode), "Order Code cannot be null or empty.");
            if (string.IsNullOrEmpty(TerminalCode))
                throw new ArgumentNullException(nameof(TerminalCode), "TerminalCode  cannot be null or empty.");
            if (string.IsNullOrEmpty(merchantCode))
                throw new ArgumentNullException(nameof(merchantCode), "Merchant Code cannot be null or empty.");
            if (string.IsNullOrEmpty(secretKey))
                throw new ArgumentNullException(nameof(secretKey), "Secret Key cannot be null or empty.");
            //string data = "30C0A513498CA1876F0210F4D608CFB5VNP20220819000001|toet|1165089|1225194|33000|https://vnpay.vn/success|https://vnpay.vn/cancel|POS-2120240001||VNPAY_QRCODE|33000 ";
            string data = $"{secretKey}{orderCode}|{userId}|{TerminalCode}|{merchantCode}|{TotalAmt}|{sucessUrl}|{cancelUrl}|{ClientTransCode}|{merchantMethodCode}|{MethodCode}|{TotalAmt}";

            using (var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(secretKey)))
            {
                byte[] hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
                return Convert.ToBase64String(hashBytes);
            }
        }
        //end
        // generate a order detail checksum 
        private string GenerateGetOrderChecksum(string orderCode, string TerminalCode, string merchantCode, string paymentRequestID, string querykey)
        {

            if (string.IsNullOrEmpty(orderCode))
                throw new ArgumentNullException(nameof(orderCode), "Order Code cannot be null or empty.");
            if (string.IsNullOrEmpty(TerminalCode))
                throw new ArgumentNullException(nameof(TerminalCode), "TerminalCode  cannot be null or empty.");
            if (string.IsNullOrEmpty(merchantCode))
                throw new ArgumentNullException(nameof(merchantCode), "Merchant Code cannot be null or empty.");
            if (string.IsNullOrEmpty(querykey))
                throw new ArgumentNullException(nameof(querykey), "Query Key cannot be null or empty.");
            string data = $"{querykey}{TerminalCode}|{merchantCode}|{paymentRequestID}|{orderCode}";

            using (var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(querykey)))
            {
                byte[] hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
                string checksum = Convert.ToBase64String(hashBytes);
                //encode checksum

                return HttpUtility.UrlEncode(checksum);
            }
        }
        //end
        // generate a order detail checksum 
        private string GenerateIPNChecksum(string orderCode, string transactionCode, string merchantMethodCode, Int64 TotalAmt, string responseCode, string ipnkey)
        {

            if (string.IsNullOrEmpty(orderCode))
                throw new ArgumentNullException(nameof(orderCode), "Order Code cannot be null or empty.");
            if (string.IsNullOrEmpty(transactionCode))
                throw new ArgumentNullException(nameof(transactionCode), "Transaction code  cannot be null or empty.");
            if (string.IsNullOrEmpty(merchantMethodCode))
                throw new ArgumentNullException(nameof(merchantMethodCode), "Merchant method Code cannot be null or empty.");
            if (string.IsNullOrEmpty(ipnkey))
                throw new ArgumentNullException(nameof(ipnkey), "Init Key cannot be null or empty.");
            string data = $"{ipnkey}{merchantMethodCode}|{orderCode}|{TotalAmt}|{transactionCode}|{responseCode}";

            using (var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(ipnkey)))
            {
                byte[] hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
                return Convert.ToBase64String(hashBytes);
            }
        }
        //end
        //Generate QR 
        public string GenerateQRCodeBase64(string qrContent)
        {
            using (var qrGenerator = new QRCodeGenerator())
            {
                QRCodeData qrCodeData = qrGenerator.CreateQrCode(qrContent, QRCodeGenerator.ECCLevel.Q);

                using (var qrCode = new QRCode(qrCodeData))
                {
                    Bitmap qrCodeImage = qrCode.GetGraphic(20); // 20 is the pixel size, adjust as needed

                    // Convert the Bitmap image to Base64
                    using (var memoryStream = new MemoryStream())
                    {
                        qrCodeImage.Save(memoryStream, ImageFormat.Png);
                        byte[] byteArray = memoryStream.ToArray();
                        return Convert.ToBase64String(byteArray);
                    }
                }
            }
        }
        //end 

        //Get Order Detail when scanned QR to pay 
        public string Get(string qrContent)
        {
            using (var qrGenerator = new QRCodeGenerator())
            {
                QRCodeData qrCodeData = qrGenerator.CreateQrCode(qrContent, QRCodeGenerator.ECCLevel.Q);

                using (var qrCode = new QRCode(qrCodeData))
                {
                    Bitmap qrCodeImage = qrCode.GetGraphic(20); // 20 is the pixel size, adjust as needed

                    // Convert the Bitmap image to Base64
                    using (var memoryStream = new MemoryStream())
                    {
                        qrCodeImage.Save(memoryStream, ImageFormat.Png);
                        byte[] byteArray = memoryStream.ToArray();
                        return Convert.ToBase64String(byteArray);
                    }
                }
            }
        }
        //end 
        // Implement IDisposable
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Release managed resources here
                    Console.WriteLine("Releasing managed resources...");
                }

                // Release unmanaged resources here if necessary

                _disposed = true;
            }
        }

        ~VnpayQR()
        {
            Dispose(false);
        }
        //End
    }
}
