# ✉️ Automatic Envelopes: Multi-Tenant AI Assistant Platform

Built with a **"Quality-First"** mindset by a former QA turned Backend Engineer.

> "I spent 7 years learning how software breaks so I could spend the rest of my career building systems that don't."

---

## 🚀 Overview
**Automatic Envelopes** is an advanced, scalable AI assistant platform designed to automate complex business logic, customer service, and customized user interactions via WhatsApp for multiple businesses simultaneously.

This project isn't just a bot; it's a showcase of **Modern .NET Engineering** tailored for a SaaS environment. It moves away from traditional CRUD/REST patterns toward a highly resilient, event-driven architecture that prioritizes data integrity, strict multi-tenant isolation, and developer experience.

## 🛠️ The Tech Stack
Built with the cutting-edge **.NET 10** (Preview) and **C# 14**, Automatic Envelopes leverages the "Critter Stack":

- **Marten**: Document DB and Event Store on top of PostgreSQL. Every interaction is recorded as a domain event, ensuring a perfect audit trail segregated by tenant.
- **Wolverine**: The next-gen "Message Bus" and "Mediator". It handles complex asynchronicity with elegant cascading messages and outbox patterns.
- **Microsoft.Extensions.AI**: A unified, provider-agnostic SDK for LLM integration (Ollama, OpenAI, Gemini).
- **pgvector**: Integrated directly into Marten for lightning-fast RAG (Retrieval-Augmented Generation). By feeding the AI with **verified, client-specific official documentation**, we ensure the assistants provide factual, accurate information while strictly preventing AI hallucinations—a clinical application of the "Quality-First" philosophy.

## 🛡️ Quality as a First-Class Citizen
Coming from a QA background, I believe that **untested code is legacy code**.
- **Alba**: For full-stack, in-memory component testing of the entire HTTP/Wolverine pipeline.
- **Testcontainers**: To guarantee that integration tests run against real, ephemeral PostgreSQL instances in Docker—no mocks, no "it works on my machine."
- **Vertical Slice Architecture**: Features are organized by business value, not by technical layers (Controllers/Services/Repos), reducing cognitive load and making the system exceptionally easy to scale for new clients.

## 🏗️ Architecture: The Event Pipeline
Automatic Envelopes operates on a reactor-like, tenant-aware event pipeline:
1. **Webhook Intake**: Validates WhatsApp HMAC signatures, resolves the correct client (tenant context), and persists the raw intake.
2. **NLP Detection**: Uses AI to identify language and user intent dynamically.
3. **RAG Retrieval**: Vector searches the isolated knowledge base to find contextually relevant information exclusively for that specific business.
4. **Persona Generation**: Crafts a customized response using the unique brand persona, tone, and system prompts configured for the client.
5. **Dispatch**: Safely sends the response back to the user via the Meta Cloud API.