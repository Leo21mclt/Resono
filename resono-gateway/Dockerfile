FROM python:3.12-slim

WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends \
    ffmpeg \
    curl \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Ensure runtime directories for SQLite data, audio/artwork cache, and downloads exist
RUN mkdir -p /app/data /app/cache/audio /app/cache/artwork /downloads

COPY . .

ENV PYTHONUNBUFFERED=1
ENV RESONO_HOST=0.0.0.0
ENV RESONO_PORT=8080

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD curl -f http://localhost:8080/health || exit 1

CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8080"]
