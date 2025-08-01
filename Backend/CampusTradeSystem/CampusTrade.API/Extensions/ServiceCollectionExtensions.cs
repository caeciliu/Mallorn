using CampusTrade.API.Options;
using CampusTrade.API.Services.Auth;
using CampusTrade.API.Services.File;
using CampusTrade.API.Utils.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace CampusTrade.API.Extensions;

/// <summary>
/// 服务注册扩展
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 添加JWT认证服务
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">配置</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        // 1. 配置JWT选项
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));

        // 2. 验证JWT配置
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();

        // 3. 注册Token服务
        services.AddScoped<ITokenService, TokenService>();

        // 4. 配置JWT认证
        var jwtOptions = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>();
        if (jwtOptions == null)
        {
            throw new InvalidOperationException("JWT配置未找到，请检查appsettings.json中的Jwt配置节");
        }

        // 验证JWT配置
        if (string.IsNullOrWhiteSpace(jwtOptions.SecretKey) || jwtOptions.SecretKey.Length < 32)
        {
            throw new InvalidOperationException("JWT密钥配置无效: 密钥不能为空且长度至少32个字符");
        }

        var tokenValidationParameters = TokenHelper.CreateTokenValidationParameters(jwtOptions);

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.RequireHttpsMetadata = jwtOptions.RequireHttpsMetadata;
            options.SaveToken = jwtOptions.SaveToken;
            options.TokenValidationParameters = tokenValidationParameters;

            // 自定义事件处理
            options.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = context =>
                {
                    var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
                    logger.LogWarning("JWT认证失败: {Error}", context.Exception.Message);
                    return Task.CompletedTask;
                },
                OnTokenValidated = async context =>
                {
                    var tokenService = context.HttpContext.RequestServices.GetRequiredService<ITokenService>();
                    var jti = context.Principal?.FindFirst("jti")?.Value;

                    // 检查Token是否在黑名单中
                    if (!string.IsNullOrEmpty(jti) && await tokenService.IsTokenBlacklistedAsync(jti))
                    {
                        context.Fail("Token已被撤销");
                    }
                },
                OnChallenge = context =>
                {
                    var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
                    logger.LogWarning("JWT认证质询: {Error}", context.Error);
                    return Task.CompletedTask;
                }
            };
        });

        // 5. 添加授权策略
        services.AddAuthorization(options =>
        {
            options.AddPolicy("RequireAuthenticatedUser", policy =>
            {
                policy.RequireAuthenticatedUser();
            });

            options.AddPolicy("RequireActiveUser", policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireClaim("IsActive", "True");
            });

            options.AddPolicy("RequireEmailVerified", policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireClaim("EmailVerified", "True");
            });
        });

        return services;
    }

    /// <summary>
    /// 添加认证相关服务
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddAuthenticationServices(this IServiceCollection services)
    {
        // 注册认证服务
        services.AddScoped<IAuthService, AuthService>();

        // 添加内存缓存（用于Token黑名单）
        services.AddMemoryCache();

        // 添加HTTP上下文访问器
        services.AddHttpContextAccessor();

        return services;
    }

    /// <summary>
    /// 添加CORS策略
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">配置</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddCorsPolicy(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddCors(options =>
        {
            options.AddPolicy("CampusTradeCors", policy =>
            {
                var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                    ?? new[] { "http://localhost:3000", "http://localhost:5173" };

                policy.WithOrigins(allowedOrigins)
                      .AllowAnyMethod()
                      .AllowAnyHeader()
                      .AllowCredentials()
                      .SetPreflightMaxAge(TimeSpan.FromMinutes(30));
            });

            // 添加开发环境的宽松CORS策略，支持file://协议和本地测试
            options.AddPolicy("DevelopmentCors", policy =>
            {
                policy.AllowAnyOrigin()
                      .AllowAnyMethod()
                      .AllowAnyHeader();
            });
        });

        return services;
    }

    /// <summary>
    /// 添加文件管理服务
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">配置</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddFileManagementServices(this IServiceCollection services, IConfiguration configuration)
    {
        // 配置文件存储选项
        services.Configure<FileStorageOptions>(configuration.GetSection(FileStorageOptions.SectionName));

        // 注册文件服务
        services.AddScoped<IFileService, FileService>();
        services.AddScoped<IThumbnailService, ThumbnailService>();

        // 验证文件存储选项
        services.AddSingleton<IValidateOptions<FileStorageOptions>, FileStorageOptionsValidator>();

        return services;
    }
}

/// <summary>
/// JWT选项验证器
/// </summary>
public class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        var errors = options.GetValidationErrors().ToList();

        if (errors.Any())
        {
            return ValidateOptionsResult.Fail(errors);
        }

        return ValidateOptionsResult.Success;
    }
}

/// <summary>
/// 文件存储选项验证器
/// </summary>
public class FileStorageOptionsValidator : IValidateOptions<FileStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, FileStorageOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.UploadPath))
        {
            errors.Add("上传路径不能为空");
        }

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            errors.Add("基础URL不能为空");
        }

        if (options.MaxFileSize <= 0)
        {
            errors.Add("最大文件大小必须大于0");
        }

        if (options.ThumbnailWidth <= 0 || options.ThumbnailHeight <= 0)
        {
            errors.Add("缩略图尺寸必须大于0");
        }

        if (options.ThumbnailQuality < 1 || options.ThumbnailQuality > 100)
        {
            errors.Add("缩略图质量必须在1-100之间");
        }

        if (errors.Any())
        {
            return ValidateOptionsResult.Fail(errors);
        }

        return ValidateOptionsResult.Success;
    }
}
