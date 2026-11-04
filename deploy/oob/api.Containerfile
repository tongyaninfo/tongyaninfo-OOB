# 由脚本固定到本机官方镜像摘要，构建上下文仅含公开配置和发布产物。
ARG API_BASE=mcr.microsoft.com/dotnet/aspnet:10.0
FROM ${API_BASE}
WORKDIR /app
COPY api/ ./
LABEL cn.tongyaninfo.project="oob-management"
EXPOSE 5080
ENTRYPOINT ["dotnet", "OobManagement.Api.dll"]
