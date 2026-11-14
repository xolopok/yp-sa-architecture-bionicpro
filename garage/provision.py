"""Провижининг объектного хранилища Garage через admin API.

Запускается как одноразовый сервис docker-compose после старта демона Garage:
назначает единственный узел в layout кластера, импортирует ключ доступа к
кэшу отчётов, создает бакет и выдает ключу полные права на него. Каждый шаг
сначала проверяет текущее состояние, поэтому повторные запуски безопасны.
"""

import json
import os
import sys
import time
import urllib.error
import urllib.request

ADMIN_URL = os.environ["GARAGE_ADMIN_URL"].rstrip("/")
ADMIN_TOKEN = os.environ["GARAGE_ADMIN_TOKEN"]
ZONE = os.environ.get("GARAGE_ZONE", "dc1")
CAPACITY_BYTES = int(os.environ.get("GARAGE_CAPACITY_BYTES", "1000000000"))
KEY_NAME = os.environ.get("GARAGE_KEY_NAME", "bionicpro")
ACCESS_KEY = os.environ["GARAGE_ACCESS_KEY"]
SECRET_KEY = os.environ["GARAGE_SECRET_KEY"]
BUCKET = os.environ["GARAGE_BUCKET"]


def call(method, path, payload=None):
    """Отправляет один запрос к admin API и возвращает (код, разобранный ответ)."""
    data = json.dumps(payload).encode() if payload is not None else None
    request = urllib.request.Request(ADMIN_URL + path, data=data, method=method)
    request.add_header("Authorization", "Bearer " + ADMIN_TOKEN)
    if data is not None:
        request.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            body = response.read()
            return response.status, json.loads(body) if body else None
    except urllib.error.HTTPError as error:
        body = error.read()
        try:
            return error.code, json.loads(body)
        except json.JSONDecodeError:
            return error.code, {"message": body.decode(errors="replace")}


def wait_for_admin_api():
    """Ожидает готовности admin API демона Garage."""
    deadline = time.time() + 300
    while True:
        try:
            status, _ = call("GET", "/v2/GetClusterStatus")
            if status == 200:
                return
        except (urllib.error.URLError, OSError):
            pass
        if time.time() > deadline:
            sys.exit("garage admin API is unreachable")
        time.sleep(2)


def ensure_layout():
    """Назначает единственный узел в layout кластера, если у него еще нет роли."""
    _, layout = call("GET", "/v2/GetClusterLayout")
    if layout["roles"]:
        print(f"layout version {layout['version']} already assigned")
        return

    if not layout["stagedRoleChanges"]:
        _, status = call("GET", "/v2/GetClusterStatus")
        node_id = status["nodes"][0]["id"]
        code, response = call("POST", "/v2/UpdateClusterLayout", {
            "roles": [{
                "id": node_id,
                "zone": ZONE,
                "capacity": CAPACITY_BYTES,
                "tags": [KEY_NAME],
            }],
        })
        if code >= 400:
            sys.exit(f"UpdateClusterLayout failed: {response}")

    version = layout["version"] + 1
    code, response = call("POST", "/v2/ApplyClusterLayout", {"version": version})
    if code >= 400:
        sys.exit(f"ApplyClusterLayout failed: {response}")
    print(f"layout version {version} applied")

    deadline = time.time() + 120
    while True:
        _, health = call("GET", "/v2/GetClusterHealth")
        if health.get("status") == "healthy":
            print("cluster healthy")
            return
        if time.time() > deadline:
            sys.exit("garage cluster did not become healthy")
        time.sleep(2)


def ensure_key():
    """Импортирует детерминированный ключ доступа, если его еще нет."""
    _, keys = call("GET", "/v2/ListKeys")
    if any(key["id"] == ACCESS_KEY for key in keys):
        print(f"access key {ACCESS_KEY} already imported")
        return
    code, response = call("POST", "/v2/ImportKey", {
        "accessKeyId": ACCESS_KEY,
        "secretAccessKey": SECRET_KEY,
        "name": KEY_NAME,
    })
    if code >= 400:
        sys.exit(f"ImportKey failed: {response}")
    print(f"access key {ACCESS_KEY} imported")


def ensure_bucket():
    """Создает бакет с глобальным алиасом и правами ключа."""
    _, buckets = call("GET", "/v2/ListBuckets")
    existing = next(
        (b for b in buckets if BUCKET in b["globalAliases"]), None)

    if existing is None:
        code, response = call("POST", "/v2/CreateBucket", {
            "name": BUCKET,
            "globalAlias": BUCKET,
        })
        if code >= 400 and response.get("code") != "BucketAlreadyExists":
            sys.exit(f"CreateBucket failed: {response}")
        _, buckets = call("GET", "/v2/ListBuckets")
        existing = next(
            (b for b in buckets if BUCKET in b["globalAliases"]), None)
        if existing is None:
            sys.exit("bucket alias not found after creation")
        print(f"bucket {BUCKET} created")
    else:
        print(f"bucket {BUCKET} already exists")

    code, response = call("POST", "/v2/AllowBucketKey", {
        "bucketId": existing["id"],
        "accessKeyId": ACCESS_KEY,
        "permissions": {"read": True, "write": True, "owner": True},
    })
    if code >= 400:
        sys.exit(f"AllowBucketKey failed: {response}")
    print(f"key {ACCESS_KEY} allowed on bucket {BUCKET}")


def main():
    wait_for_admin_api()
    ensure_layout()
    ensure_key()
    ensure_bucket()
    print("garage provisioning complete")


if __name__ == "__main__":
    main()