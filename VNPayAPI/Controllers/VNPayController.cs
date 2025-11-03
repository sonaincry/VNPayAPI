using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Collections.Concurrent;
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
            public string Status { get; set; } = "PROCESSING";
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
                // ---- 1. Create QR -------------------------------------------------
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

                // ---- 2. Store initial state --------------------------------------
                _transactionResults[ReceiptNo] = new GetOrderResult { Status = "PROCESSING" };

                // ---- 3. Background polling ----------------------------------------
                _ = Task.Run(async () =>
                {
                    try
                    {
                        string raw = await vnpay.WaitForGetOrderResponse(
                            orderCode, paymentRequestId, TerminalID, MerchantCode,
                            endpointGet, queryKey,
                            new CancellationTokenSource(TimeSpan.FromMinutes(5)).Token);

                        // parse once to extract status
                        using var doc = JsonDocument.Parse(raw);
                        string status = doc.RootElement
                                           .GetProperty("data")
                                           .GetProperty("transactions")[0]
                                           .GetProperty("status")
                                           .GetString() ?? "UNKNOWN";

                        // keep the *full* JSON for the client
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

        [HttpGet("GetResult")]
        public IActionResult GetResult(string ReceiptNo)
        {
            if (!_transactionResults.TryGetValue(ReceiptNo, out var result))
                return NotFound(new { message = "No transaction result yet." });

            // ------------------------------------------------------------
            // 1. STILL PROCESSING → return plain text (no EDC)
            // ------------------------------------------------------------
            if (result.Status == "PROCESSING")
            {
                return Content("PROCESSING", "text/plain");
            }

            // ------------------------------------------------------------
            // 2. FAILED / CANCELLED → short EDC error string
            // ------------------------------------------------------------
            if (result.Status == "FAILED" || result.Status == "CANCELLED")
            {
                string respCode = result.Status == "FAILED" ? "96" : "68";
                string edc = BuildEdcError(respCode);
                return Content(edc, "text/plain");
            }

            // ------------------------------------------------------------
            // 3. SUCCESS → full EDC string (only fields VNPAY returns)
            // ------------------------------------------------------------
            if (result.Status == "SUCCESS" && !string.IsNullOrEmpty(result.RawJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(result.RawJson);
                    var txn = doc.RootElement
                                 .GetProperty("data")
                                 .GetProperty("transactions")[0];

                    string refNo = txn.GetProperty("transactionCode").GetString() ?? "";
                    string invoice = txn.GetProperty("orderCode").GetString() ?? "";
                    string amountStr = txn.GetProperty("amount").GetInt64()
                                               .ToString().PadLeft(12, '0');
                    string paymentTimeStr = txn.GetProperty("paymentTime").GetString() ?? "";
                    string partnerMch = txn.GetProperty("partnerMerchantCode").GetString() ?? "";
                    string bankCode = txn.GetProperty("bankCode").GetString() ?? "VNPAYEWALLET";

                    DateTime paymentDt = DateTime.TryParse(paymentTimeStr, out var dt)
                                         ? dt : DateTime.UtcNow.AddHours(7);

                    string edc = BuildEdcSuccess(
                        refNo: refNo,
                        invoice: invoice,
                        amount: amountStr,
                        paymentTime: paymentDt,
                        partnerMch: partnerMch,
                        bankCode: bankCode);

                    return Content(edc, "text/plain");
                }
                catch (Exception ex)
                {
                    return StatusCode(500, new { message = "Parse error", error = ex.Message });
                }
            }

            // fallback
            return Content("UNKNOWN", "text/plain");
        }

        private string BuildEdcSuccess(
    string refNo, string invoice, string amount,
    DateTime paymentTime, string partnerMch, string bankCode)
        {
            const string app = "VNPAY";
            const string procCode = "000000";        
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
        $"MERCHANT_CODE:{partnerMch}",
        $"CARD_TYPE:{cardType}",
        $"PAN:{pan}",
        $"NAME:{name}",
        $"BILL_ID:{invoice}",
        $"PosTerminalID:;",
        $"AMOUNT:{amount}",
        $"SEND:OK;"
    };

            return "\x02\x01" + string.Join(";", parts) + "\x03@";
        }

        private string BuildEdcError(string responseCode)   // 96 = FAILED, 68 = CANCELLED
        {
            var parts = new[]
            {
        "/APP:VNPAY",
        "PROC_CODE :000000",
        $"DATE:{DateTime.UtcNow.AddHours(7):ddMM}",
        $"TIME:{DateTime.UtcNow.AddHours(7):HHmmss}",
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
        "SEND:OK;"
    };

            return "\x02\x01" + string.Join(";", parts) + "\x03@";
        }
    }
    }
