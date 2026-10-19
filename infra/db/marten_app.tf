resource "random_password" "marten_app" {
  length  = 32
  special = false
}

resource "aws_secretsmanager_secret" "marten_app" {
  name = "${var.project_name}/marten-app"
}

resource "aws_secretsmanager_secret_version" "marten_app" {
  secret_id = aws_secretsmanager_secret.marten_app.id
  secret_string = jsonencode({
    username = postgresql_role.marten_app.name
    password = random_password.marten_app.result
  })
}

resource "postgresql_role" "marten_app" {
  name                = "marten_app"
  login               = true
  password            = random_password.marten_app.result
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
