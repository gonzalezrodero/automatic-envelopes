variable "aws_region" {
  description = "AWS region to deploy to"
  type        = string
}

variable "project_name" {
  description = "Name of the project"
  type        = string
}

variable "db_instance_class" {
  description = "The instance type of the RDS instance"
  type        = string
}

variable "multi_az" {
  description = "Specifies if the RDS instance is multi-AZ"
  type        = bool
}

variable "allocated_storage" {
  description = "The allocated storage in gigabytes"
  type        = number
}

variable "backup_retention_period" {
  description = "Days of automated RDS backups to keep. Prod stays at 7 or more; dev overrides this."
  type        = number
  default     = 7
}

variable "deletion_protection" {
  description = "Block deletion of the RDS instance. Prod keeps the default."
  type        = bool
  default     = true
}

variable "skip_final_snapshot" {
  description = "Skip the snapshot taken when the instance is destroyed. Prod keeps the default."
  type        = bool
  default     = false
}

variable "rds_auto_start" {
  description = "If true, EventBridge starts RDS at 08:00 Europe/Madrid. Keep false while the app is unused."
  type        = bool
  default     = false
}

variable "aws_account_id" {
  type    = string
  default = "543704476214"
}

variable "db_master_password" {
  description = "RDS master password. Ephemeral, so Terraform omits it from the plan and state. PR plans leave it unset."
  type        = string
  sensitive   = true
  ephemeral   = true
  default     = null
}