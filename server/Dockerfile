# Build from the repository root so that ../site is available:
#   docker build -f server/Dockerfile -t proxybridge-server .
FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

WORKDIR /app/server

COPY server/requirements.txt ./requirements.txt
RUN pip install --no-cache-dir -r requirements.txt

COPY server/app ./app
COPY site /app/site

RUN mkdir -p /app/server/data
VOLUME ["/app/server/data"]

EXPOSE 8080
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8080"]
