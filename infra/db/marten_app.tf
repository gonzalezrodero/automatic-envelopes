ephemeral "random_password" "marten_app" {
  length  = 32
  special = false
}

locals {
  # Bump to rotate. Both resources use it, so the role and the secret move together.
  marten_app_password_version = 1
}

resource "aws_secretsmanager_secret" "marten_app" {
  name = "${var.project_name}/marten-app"
}

resource "aws_secretsmanager_secret_version" "marten_app" {
  secret_id = aws_secretsmanager_secret.marten_app.id
  secret_string_wo = jsonencode({
    username = postgresql_role.marten_app.name
    password = ephemeral.random_password.marten_app.result
  })
  secret_string_wo_version = local.marten_app_password_version
}

resource "postgresql_role" "marten_app" {
  name                = "marten_app"
  login               = true
  password_wo         = ephemeral.random_password.marten_app.result
  password_wo_version = tostring(local.marten_app_password_version)
  superuser           = false
  create_database     = false
  create_role         = false
  skip_drop_role      = true
  skip_reassign_owned = true
}

resource "postgresql_grant" "connect" {
  database    = aws_db_instance.postgres.db_name
  role        = postgresql_role.marten_app.name
  object_type = "database"
  privileges  = ["CONNECT"]
}

resource "postgresql_grant" "schema" {
  database    = aws_db_instance.postgres.db_name
  role        = postgresql_role.marten_app.name
  schema      = "public"
  object_type = "schema"
  privileges  = ["USAGE", "CREATE"]
}

resource "postgresql_grant" "tables" {
  database    = aws_db_instance.postgres.db_name
  role        = postgresql_role.marten_app.name
  schema      = "public"
  object_type = "table"
  privileges  = ["SELECT", "INSERT", "UPDATE", "DELETE"]
}

resource "postgresql_grant" "sequences" {
  database    = aws_db_instance.postgres.db_name
  role        = postgresql_role.marten_app.name
  schema      = "public"
  object_type = "sequence"
  privileges  = ["USAGE", "SELECT", "UPDATE"]
}

resource "postgresql_default_privileges" "tables" {
  database    = aws_db_instance.postgres.db_name
  role        = postgresql_role.marten_app.name
  owner       = aws_db_instance.postgres.username
  schema      = "public"
  object_type = "table"
  privileges  = ["SELECT", "INSERT", "UPDATE", "DELETE"]
}

resource "postgresql_default_privileges" "sequences" {
  database    = aws_db_instance.postgres.db_name
  role        = postgresql_role.marten_app.name
  owner       = aws_db_instance.postgres.username
  schema      = "public"
  object_type = "sequence"
  privileges  = ["USAGE", "SELECT", "UPDATE"]
}
