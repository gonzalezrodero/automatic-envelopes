# Database Production Variables
project_name            = "automatic-envelopes"
aws_region              = "eu-west-1"
db_instance_class       = "db.t3.micro"
multi_az                = false
allocated_storage       = 20
backup_retention_period = 7
deletion_protection     = true
skip_final_snapshot     = false
copy_tags_to_snapshot   = true
rds_auto_start          = false
aws_account_id          = "149168707361"