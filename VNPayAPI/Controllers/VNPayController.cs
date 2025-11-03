using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using vnpay;

namespace VNPayAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class VNPayController : ControllerBase
    {
        private static readonly ConcurrentDictionary<string, GetOrderResult> _transactionResults = new();

        public class GetOrderResult
        {
            public string Status { get; set; } = "PROCESSING"; // SUCCESS, FAILED, CANCELLED, PROCESSING
            public string RawJson { get; set; } = string.Empty;
        }

        [HttpGet("DoSalesVNPAY")]
        public async Task<IActionResult> DoSalesVNPAY(
            string TerminalID,
            string MerchantCode,
            string ReceiptNo,
            long Amount,
            string SuccessUrl = "https://vnpay.vn/success",
            string CancelUrl = "https://vnpay.vn/cancel",
            string UserId = "POS001")
        {
            const string endpointInit = "https://spos-api.vnpaytest.vn/external/merchantorder";
            const string endpointGet = "https://spos-api.vnpaytest.vn/external/getorderdetail";
            const string initKey = "30C0A513498CA1876F0210F4D608CFB5";
            const string queryKey = "8B34ED0D17F45489A3C8DB1BEF73B5CC";

            using var vnpay = new VnpayQR();

            try
            {
                // 1. Create QR
                string jsonRequest = vnpay.RequestSale(
                    UserId, ReceiptNo, TerminalID, MerchantCode, Amount,
                    SuccessUrl, CancelUrl, "", "VNPAY_QRCODE", initKey);

                string initResp = await vnpay.SendPostRequestSale(jsonRequest, endpointInit);
                var initJson = JsonConvert.DeserializeObject<dynamic>(initResp);

                if (initJson?.payments?.qr?.responseCode != 200)
                    return BadRequest(new { message = "Failed to initialize QR.", rawResponse = initResp });

                string orderCode = initJson?.orderCode;
                string paymentRequestId = initJson?.paymentRequestId;
                string qrContent = initJson?.payments?.qr?.qrContent;

                if (string.IsNullOrEmpty(qrContent))
                    return BadRequest(new { message = "QR content missing.", rawResponse = initResp });

                string qrBase64 = vnpay.GenerateQRCodeBase64(qrContent);

                _transactionResults[ReceiptNo] = new GetOrderResult { Status = "PROCESSING" };

                _ = Task.Run(async () =>
                {
                    try
                    {
                        string raw = await vnpay.WaitForGetOrderResponse(
                            orderCode, paymentRequestId, TerminalID, MerchantCode,
                            endpointGet, queryKey,
                            new CancellationTokenSource(TimeSpan.FromMinutes(5)).Token);

                        using var doc = JsonDocument.Parse(raw);
                        string status = doc.RootElement
                                           .GetProperty("data")
                                           .GetProperty("transactions")[0]
                                           .GetProperty("status")
                                           .GetString() ?? "UNKNOWN";

                        _transactionResults[ReceiptNo] = new GetOrderResult
                        {
                            Status = status,
                            RawJson = raw
                        };
                    }
                    catch
                    {
                        _transactionResults[ReceiptNo] = new GetOrderResult { Status = "FAILED" };
                    }
                });

                return Ok(new
                {
                    orderCode,
                    paymentRequestId,
                    qrBase64,
                    status = "PROCESSING",
                    message = "QR generated successfully. Waiting for customer to scan."
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPost("CancelOrder")]
        public async Task<IActionResult> CancelOrder(
            string TerminalID,
            string MerchantCode,
            string ReceiptNo,
            string Reason = "Customer cancel")
        {
            const string endpoint = "https://spos-api.vnpaytest.vn/external/cancelorder";
            const string secretKey = "30C0A513498CA1876F0210F4D608CFB5";

            using var vnpay = new VnpayQR();

            try
            {
                string jsonRequest = vnpay.RequestCancel(ReceiptNo, TerminalID, MerchantCode, Reason, secretKey);
                string rawResponse = await vnpay.SendCancelRequest(jsonRequest, endpoint);

                // Parse VNPAY code
                int vnpayCode = 500;
                try
                {
                    using var doc = JsonDocument.Parse(rawResponse);
                    vnpayCode = doc.RootElement.GetProperty("code").GetInt32();
                }
                catch { }

                // Store result
                _transactionResults[ReceiptNo] = new GetOrderResult
                {
                    Status = vnpayCode == 200 ? "CANCELLED" : "FAILED",
                    RawJson = rawResponse
                };

                // Build EDC using shared logic
                string edc = BuildEdcFromResult(_transactionResults[ReceiptNo], ReceiptNo, TerminalID, MerchantCode);
                return Content(edc, "text/plain");
            }
            catch (Exception ex)
            {
                _transactionResults[ReceiptNo] = new GetOrderResult { Status = "FAILED", RawJson = ex.Message };
                string edc = BuildEdcError("500", "LOCAL_ERROR");
                return Content(edc, "text/plain");
            }
        }

        [HttpGet("GetResult")]
        public IActionResult GetResult(string ReceiptNo)
        {
            if (!_transactionResults.TryGetValue(ReceiptNo, out var result))
                return NotFound(new { message = "No transaction result yet." });

            string edc = BuildEdcFromResult(result, ReceiptNo, "18536307", "MCVJ");
            return Content(edc, "text/plain");
        }

        // === UNIFIED EDC BUILDER (Option B) ===
        private string BuildEdcFromResult(GetOrderResult result, string invoice, string terminalId, string merchantCode)
        {
            // 1. PROCESSING
            if (result.Status == "PROCESSING")
                return "PROCESSING";

            // 2. SUCCESS
            if (result.Status == "SUCCESS" && !string.IsNullOrEmpty(result.RawJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(result.RawJson);
                    var txn = doc.RootElement.GetProperty("data").GetProperty("transactions")[0];

                    string refNo = txn.GetProperty("transactionCode").GetString() ?? "";
                    string amountStr = txn.GetProperty("amount").GetInt64().ToString().PadLeft(12, '0');
                    string paymentTimeStr = txn.GetProperty("paymentTime").GetString() ?? "";
                    string partnerMch = txn.GetProperty("partnerMerchantCode").GetString() ?? "";
                    DateTime paymentDt = DateTime.TryParse(paymentTimeStr, out var dt)
                        ? dt : DateTime.UtcNow.AddHours(7);

                    return BuildEdcSuccess(refNo, invoice, amountStr, paymentDt, partnerMch, "VNPAYEWALLET");
                }
                catch
                {
                    return BuildEdcError("500", "PARSE_ERROR");
                }
            }

            // 3. CANCELLED → treat as success with 0 amount
            if (result.Status == "CANCELLED")
            {
                return BuildEdcCancelSuccess(invoice, terminalId, merchantCode);
            }

            // 4. FAILED / CANCEL ERROR → extract VNPAY code from RawJson
            int vnpayCode = 500;
            string errorMsg = "UNKNOWN_ERROR";

            if (!string.IsNullOrEmpty(result.RawJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(result.RawJson);
                    vnpayCode = doc.RootElement.GetProperty("code").GetInt32();
                    errorMsg = doc.RootElement.GetProperty("message").GetString() ?? "UNKNOWN";
                }
                catch { }
            }

            // Map VNPAY code → EDC
            return vnpayCode switch
            {
                200 => BuildEdcCancelSuccess(invoice, terminalId, merchantCode),
                400001 => BuildEdcError("400001", "MISSING_FIELDS"),
                400303 => BuildEdcError("400303", "INVALID_CHECKSUM"),
                400310 => BuildEdcError("400310", "CANNOT_CANCEL_PAID"),
                400311 => BuildEdcError("400311", "INVALID_DATA"),
                500 => BuildEdcError("500", "SYSTEM_ERROR"),
                _ => BuildEdcError(vnpayCode.ToString(), errorMsg)
            };
        }

        // === EDC HELPERS ===
        private string BuildEdcSuccess(
            string refNo, string invoice, string amount,
            DateTime paymentTime, string partnerMch, string bankCode)
        {
            const string app = "VNPAY";
            const string procCode = "000000";
            const string terminalId = "18536307";
            const string pan = "************";
            const string name = "/";
            const string cardType = "VNPAY_QR";

            string date = paymentTime.ToString("ddMM");
            string time = paymentTime.ToString("HHmmss");

            var parts = new[]
            {
                $"/APP:{app}",
                $"PROC_CODE :{procCode}",
                $"DATE:{date}",
                $"TIME:{time}",
                $"REF_NO:{refNo}",
                $"APPV_CODE :000000",
                $"RESPONSE_CODE:00",
                $"TERMINAL_ID:{terminalId}",
                $"MERCHANT_CODE:{partnerMch}",
                $"CARD_TYPE:{cardType}",
                $"PAN:{pan}",
                $"NAME:{name}",
                $"INVOICE:{invoice}",
                $"BILL_ID:{invoice}",
                $"PosTerminalID:;",
                $"AMOUNT:{amount}",
                $"SEND:OK;"
            };

            return "\x02\x01" + string.Join(";", parts) + "\x03@";
        }

        private string BuildEdcError(string responseCode, string errorDetail)
        {
            string date = DateTime.UtcNow.AddHours(7).ToString("ddMM");
            string time = DateTime.UtcNow.AddHours(7).ToString("HHmmss");

            var parts = new[]
            {
                "/APP:VNPAY",
                "PROC_CODE :000000",
                $"DATE:{date}",
                $"TIME:{time}",
                "REF_NO:",
                "APPV_CODE :",
                $"RESPONSE_CODE:{responseCode}",
                "TERMINAL_ID:18536307",
                "MERCHANT_CODE:",
                "CARD_TYPE:VNPAY_QR",
                "PAN:************",
                "NAME:/",
                "INVOICE:",
                "BILL_ID:",
                "PosTerminalID:;",
                "AMOUNT:000000000000",
                $"ERROR_MSG:{errorDetail};",
                "SEND:OK;"
            };

            return "\x02\x01" + string.Join(";", parts) + "\x03@";
        }

        private string BuildEdcCancelSuccess(string invoice, string terminalId, string merchantCode)
        {
            string date = DateTime.UtcNow.AddHours(7).ToString("ddMM");
            string time = DateTime.UtcNow.AddHours(7).ToString("HHmmss");

            var parts = new[]
            {
                "/APP:VNPAY",
                "PROC_CODE :000000",
                $"DATE:{date}",
                $"TIME:{time}",
                "REF_NO:",
                "APPV_CODE :000000",
                "RESPONSE_CODE:00",
                $"TERMINAL_ID:{terminalId.PadLeft(8, '0')}",
                $"MERCHANT_CODE:{merchantCode}",
                "CARD_TYPE:VNPAY_QR",
                "PAN:************",
                "NAME:/",
                $"INVOICE:{invoice}",
                $"BILL_ID:{invoice}",
                "PosTerminalID:;",
                "AMOUNT:000000000000",
                "SEND:OK;"
            };

            return "\x02\x01" + string.Join(";", parts) + "\x03@";
        }
    }
}