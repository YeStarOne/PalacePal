using Account;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using static Account.AccountService;

namespace Pal.Server.Services
{
    internal class AccountService : AccountServiceBase
    {
        private readonly ILogger<AccountService> _logger;
        private readonly PalContext _dbContext;
        private readonly string _tokenIssuer;
        private readonly string _tokenAudience;
        private readonly SymmetricSecurityKey _signingKey;

        private byte[]? _salt;

        public AccountService(ILogger<AccountService> logger, IConfiguration configuration, PalContext dbContext)
        {
            _logger = logger;
            _dbContext = dbContext;


            _tokenIssuer = configuration["JWT:Issuer"];
            _tokenAudience = configuration["JWT:Audience"];
            _signingKey = new SymmetricSecurityKey(Convert.FromBase64String(configuration["JWT:Key"]));
        }

        [AllowAnonymous]
        public override async Task<CreateAccountReply> CreateAccount(CreateAccountRequest request, ServerCallContext context)
        {
            try
            {
                var remoteIp = context.GetHttpContext().Connection.RemoteIpAddress;
                if (remoteIp != null && (remoteIp.ToString().StartsWith("127.") || remoteIp.ToString() == "::1"))
                {
                    remoteIp = null;
                    foreach (var header in context.RequestHeaders)
                    {
                        if (header.Key == "x-real-ip")
                        {
                            remoteIp = IPAddress.Parse(header.Value);
                            break;
                        }
                    }
                }
                if (remoteIp == null)
                    return new CreateAccountReply { Success = false };

                _salt ??= Convert.FromBase64String((await _dbContext.GlobalSettings.FindAsync(new object[] { "salt" }, cancellationToken: context.CancellationToken))!.Value);
                var ipHash = Convert.ToBase64String(new Rfc2898DeriveBytes(remoteIp.GetAddressBytes(), _salt, iterations: 10000).GetBytes(24));

                Account? existingAccount = await _dbContext.Accounts.FirstOrDefaultAsync(a => a.IpHash == ipHash, cancellationToken: context.CancellationToken);
                if (existingAccount != null)
                {
                    _logger.LogInformation("CreateAccount: Returning existing account {AccountId} for ip hash {IpHash} ({Ip})", existingAccount.Id, ipHash, remoteIp.ToString().Substring(0, 5));
                    return new CreateAccountReply { Success = true, AccountId = existingAccount.Id.ToString() };
                }


                Account newAccount = new Account
                {
                    Id = Guid.NewGuid(),
                    IpHash = ipHash,
                    CreatedAt = DateTime.Now,
                };
                _dbContext.Accounts.Add(newAccount);
                await _dbContext.SaveChangesAsync(context.CancellationToken);

                _logger.LogInformation("CreateAccount: Created new account {AccountId} for ip hash {IpHash}", newAccount.Id, ipHash);
                return new CreateAccountReply
                {
                    Success = true,
                    AccountId = newAccount.Id.ToString(),
                };
            }
            catch (Exception e)
            {
                _logger.LogError("Could not create account: {e}", e);
                return new CreateAccountReply { Success = false };
            }
        }

        [AllowAnonymous]
        public override async Task<LoginReply> Login(LoginRequest request, ServerCallContext context)
        {
            try
            {
                if (!Guid.TryParse(request.AccountId, out Guid accountId))
                {
                    _logger.LogWarning("Submitted account id '{AccountId}' is not a valid id", request.AccountId);
                    return new LoginReply { Success = false, Error = LoginError.Unknown };
                }

                var existingAccount = await _dbContext.Accounts.FindAsync(new object[] { accountId }, cancellationToken: context.CancellationToken);
                if (existingAccount == null)
                {
                    _logger.LogWarning("Could not find account with id '{AccountId}'", accountId);
                    return new LoginReply { Success = false, Error = LoginError.InvalidAccountId };
                }

                var tokenHandler = new JwtSecurityTokenHandler();
                var tokenDescriptor = new SecurityTokenDescriptor
                {
                    Subject = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, accountId.ToString()) }),
                    Expires = DateTime.Now.AddDays(1),
                    Issuer = _tokenIssuer,
                    Audience = _tokenAudience,
                    SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256Signature),
                };

                return new LoginReply
                {
                    Success = true,
                    AuthToken = tokenHandler.WriteToken(tokenHandler.CreateToken(tokenDescriptor)),
                    ExpiresAt = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(1).AddMinutes(-5)),
                };
            } 
            catch (Exception e)
            {
                _logger.LogError("Could not log into account {Account}: {e}", request.AccountId, e);
                return new LoginReply { Success = false, Error = LoginError.Unknown };
            }
        }

        [Authorize]
        public override Task<VerifyReply> Verify(VerifyRequest request, ServerCallContext context)
        {
            var _ = context.GetAccountId();
            return Task.FromResult(new VerifyReply());
        }
    }
}