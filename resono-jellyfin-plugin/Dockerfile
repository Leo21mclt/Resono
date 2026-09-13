FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["Resono.Plugin.csproj", "./"]
RUN dotnet restore "Resono.Plugin.csproj"

COPY . .
RUN dotnet build "Resono.Plugin.csproj" -c Release -o /app/build

FROM scratch AS export
COPY --from=build /app/build/Resono.Plugin.dll /
