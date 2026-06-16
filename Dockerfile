# STAGE 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 1. Copy ONLY the project file to restore dependencies
COPY src/AutomaticEnvelopes.Api/AutomaticEnvelopes.Api.csproj AutomaticEnvelopes.Api/
WORKDIR /src/AutomaticEnvelopes.Api
RUN dotnet restore

# 2. Copy the rest of the source code
COPY src/AutomaticEnvelopes.Api/ .

# 3. Clean up binaries
RUN rm -rf bin/ obj/

# 4. CRITTER STACK: Pre-generate Marten and Wolverine code
RUN dotnet run --project AutomaticEnvelopes.Api.csproj -- codegen write

# 5. Publish the application
RUN dotnet publish AutomaticEnvelopes.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

# STAGE 2: Runtime (AWS Lambda official)
FROM public.ecr.aws/lambda/dotnet:10 AS final
WORKDIR /var/task

# Copy compiled binaries from the build stage
COPY --from=build /app/publish .