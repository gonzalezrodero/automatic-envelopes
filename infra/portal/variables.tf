variable "aws_region" {
  description = "AWS region of the Terraform state and the API"
  type        = string
}

variable "aws_account_id" {
  description = "AWS account that holds this environment's Terraform state and API"
  type        = string
}

variable "cloudflare_account_id" {
  description = "Cloudflare account that owns the Pages project. Set TF_VAR_cloudflare_account_id from the GitHub variable CLOUDFLARE_ACCOUNT_ID."
  type        = string
}

variable "cloudflare_api_token" {
  description = "Cloudflare API token with Pages write. Set TF_VAR_cloudflare_api_token from the GitHub secret CLOUDFLARE_API_TOKEN."
  type        = string
  sensitive   = true
}

variable "pages_project_name" {
  description = "Cloudflare Pages project name"
  type        = string
}

variable "portal_hostname" {
  description = "Custom hostname for the admin portal. Empty skips the DNS attachment."
  type        = string
  default     = ""
}

variable "vite_api_base" {
  description = "API origin baked into the portal. Empty uses the Lambda function URL from the serverless stack."
  type        = string
  default     = ""
}

variable "cognito_domain" {
  description = "Cognito Hosted UI host, without a scheme"
  type        = string
}

variable "github_owner" {
  description = "GitHub owner of the portal repository"
  type        = string
}

variable "github_repo" {
  description = "GitHub repository that Cloudflare Pages builds"
  type        = string
}
