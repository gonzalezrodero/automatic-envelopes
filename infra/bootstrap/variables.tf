variable "aws_region" {
  description = "AWS region to deploy to"
  type        = string
}

variable "project_name" {
  description = "Name of the project"
  type        = string
}

variable "aws_account_id" {
  type    = string
  default = "543704476214"
}

variable "github_owner" {
  default = "gonzalezrodero"
}

variable "github_repo" {
  default = "automatic-envelopes"
}