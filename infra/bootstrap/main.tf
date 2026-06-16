# ==========================================
# 1. LOGGING BUCKET (Store logs securely)
# ==========================================
resource "aws_s3_bucket" "tf_state_logs" {
  bucket        = "${var.project_name}-tf-state-logs-${var.aws_account_id}"
  force_destroy = false
}

# Block all public access
resource "aws_s3_bucket_public_access_block" "tf_state_logs_access" {
  bucket                  = aws_s3_bucket.tf_state_logs.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

# Enforce Object Ownership (Modern AWS best practice for logs)
resource "aws_s3_bucket_ownership_controls" "tf_state_logs_ownership" {
  bucket = aws_s3_bucket.tf_state_logs.id
  rule {
    object_ownership = "BucketOwnerEnforced"
  }
}

# Enforce HTTPS-only AND allow AWS to write logs here
resource "aws_s3_bucket_policy" "tf_state_logs_policy" {
  bucket = aws_s3_bucket.tf_state_logs.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid       = "EnforceHTTPS"
        Effect    = "Deny"
        Principal = "*"
        Action    = "s3:*"
        Resource = [
          aws_s3_bucket.tf_state_logs.arn,
          "${aws_s3_bucket.tf_state_logs.arn}/*"
        ]
        Condition = {
          Bool = { "aws:SecureTransport" = "false" }
        }
      },
      {
        Sid       = "AllowS3ServerAccessLogs"
        Effect    = "Allow"
        Principal = { Service = "logging.s3.amazonaws.com" }
        Action    = "s3:PutObject"
        Resource  = "${aws_s3_bucket.tf_state_logs.arn}/*"
        Condition = {
          ArnLike      = { "aws:SourceArn" = "arn:aws:s3:::${var.project_name}-tf-state-${var.aws_account_id}" }
          StringEquals = { "aws:SourceAccount" = var.aws_account_id }
        }
      }
    ]
  })
}

# ==========================================
# 2. MAIN STATE BUCKET (Store Terraform State)
# ==========================================
resource "aws_s3_bucket" "tf_state" {
  bucket        = "${var.project_name}-tf-state-${var.aws_account_id}"
  force_destroy = false
}

# Enable logging and point it to the logging bucket (Fixes SQ S6258)
resource "aws_s3_bucket_logging" "tf_state_logging" {
  bucket        = aws_s3_bucket.tf_state.id
  target_bucket = aws_s3_bucket.tf_state_logs.id
  target_prefix = "logs/"
}

# Block all public access
resource "aws_s3_bucket_public_access_block" "tf_state_access" {
  bucket                  = aws_s3_bucket.tf_state.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

# Enforce HTTPS-only (Fixes SQ S6249)
resource "aws_s3_bucket_policy" "tf_state_force_ssl" {
  bucket = aws_s3_bucket.tf_state.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid       = "EnforceHTTPS"
        Effect    = "Deny"
        Principal = "*"
        Action    = "s3:*"
        Resource = [
          aws_s3_bucket.tf_state.arn,
          "${aws_s3_bucket.tf_state.arn}/*"
        ]
        Condition = {
          Bool = { "aws:SecureTransport" = "false" }
        }
      }
    ]
  })
}